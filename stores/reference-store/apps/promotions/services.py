"""
Pricing a basket against the promotions a store has running.

Two things live here. The first is the base store's own pricing: coupons,
percentage and fixed discounts, validity windows and minimums. The second is the
**seam** the `advanced-promotions` Feature plugs into.

The direction of that seam matters and is not symmetric. The base store may look
for an installed Feature; a Feature may never import the base store's business
code ([`feature-authoring.md`](../../../../docs/feature-authoring.md)). So the
Feature owns its own tables and exposes a function taking plain data, and this
module decides whether to call it and what to do with the answer. That keeps
uninstalling the Feature a matter of the import failing, rather than of the base
store having been built around it.
"""

from __future__ import annotations

from dataclasses import dataclass
from decimal import Decimal

from django.db import transaction
from django.utils import timezone

from .models import Coupon, Promotion, PromotionStatus


@dataclass(frozen=True)
class BasketLine:
    """
    One line of a basket, as the pricing rules see it.

    Plain data, deliberately: it is what gets handed across the seam to an
    installed Feature, and a Feature that received catalogue models would be
    importing the base store's business code through the back door.
    """

    product_id: int
    quantity: int
    unit_price: Decimal

    @property
    def line_total(self) -> Decimal:
        return self.unit_price * self.quantity


@dataclass(frozen=True)
class DiscountOutcome:
    """
    What pricing decided, in terms an order can snapshot.

    Deliberately plain data with no model in it: `orders.OrderPromotion` copies
    these fields verbatim, and it has to stay meaningful after the rule that
    produced it has moved, been archived, or been uninstalled with its Feature.
    """

    promotion_id: int | None
    coupon_id: int | None
    promotion_name: str
    coupon_code: str
    discount_type: str
    discount_value: Decimal
    discount_amount: Decimal

    @property
    def applies(self) -> bool:
        return self.discount_amount > Decimal("0")


NOTHING = DiscountOutcome(None, None, "", "", "", Decimal("0"), Decimal("0"))


def price(
    subtotal: Decimal,
    *,
    coupon_code: str | None = None,
    lines: list[BasketLine] | None = None,
    at=None,
) -> DiscountOutcome:
    """
    Finds the best discount available for this basket.

    A presented code is honoured exclusively: a shopper who typed one expects
    that one to be used, and silently substituting a better automatic promotion
    would be a support call even though it saved them money.

    When the `advanced-promotions` Feature is installed, its rules are consulted
    too and the better of the two wins unless the Feature says its rule stacks.
    Without it, this returns exactly what it returned before the Feature existed.

    Returns `NOTHING` rather than raising when nothing applies. Not qualifying is
    an ordinary outcome of pricing, and callers that had to catch an exception
    for it would be worse for it.
    """
    moment = at or timezone.now()

    base = _from_coupon(subtotal, coupon_code, moment) if coupon_code else _best_automatic(subtotal, moment)

    return _combine(base, _advanced(subtotal, lines, moment), subtotal)


def _from_coupon(subtotal: Decimal, code: str, moment) -> DiscountOutcome:
    coupon = (
        Coupon.objects.select_related("promotion")
        .filter(normalized_code="".join(code.split()).upper())
        .first()
    )

    if coupon is None or not coupon.is_redeemable(moment):
        return NOTHING

    amount = coupon.promotion.discount_for(subtotal)

    if amount <= Decimal("0"):
        return NOTHING

    return DiscountOutcome(
        promotion_id=coupon.promotion_id,
        coupon_id=coupon.pk,
        promotion_name=coupon.promotion.name,
        coupon_code=coupon.code,
        discount_type=coupon.promotion.discount_type,
        discount_value=coupon.promotion.discount_value,
        discount_amount=amount,
    )


def _best_automatic(subtotal: Decimal, moment) -> DiscountOutcome:
    """
    The best promotion that needs no code.

    Ordered by priority and then by the discount itself, so a merchant can
    override the arithmetic when they want a particular campaign to win.
    """
    candidates = Promotion.objects.filter(
        status=PromotionStatus.ACTIVE,
        requires_coupon=False,
    ).order_by("-priority", "id")

    best = NOTHING

    for promotion in candidates:
        if not promotion.is_live(moment):
            continue

        amount = promotion.discount_for(subtotal)

        if amount > best.discount_amount:
            best = DiscountOutcome(
                promotion_id=promotion.pk,
                coupon_id=None,
                promotion_name=promotion.name,
                coupon_code="",
                discount_type=promotion.discount_type,
                discount_value=promotion.discount_value,
                discount_amount=amount,
            )

    return best


def _advanced(subtotal: Decimal, lines: list[BasketLine] | None, moment):
    """
    The `advanced-promotions` Feature's answer, or None when it is not installed.

    Asked through Django's app registry rather than by catching ImportError.
    The two are different states: a package pip has put on the path but the
    installer never registered is importable and its models raise from the model
    metaclass instead, which is a much worse failure to meet inside checkout.

    Any failure here loses the advanced rules and keeps the sale. A store whose
    checkout breaks because an optional Feature misbehaved has turned an upsell
    into an outage.
    """
    from django.apps import apps as django_apps

    if not django_apps.is_installed("knight_feature_promotions"):
        return None

    try:
        from knight_feature_promotions import services as advanced

        return advanced.price(subtotal, lines=lines or [], at=moment)
    except Exception:  # noqa: BLE001 - an optional feature must never break checkout
        import logging

        logging.getLogger(__name__).exception(
            "The advanced-promotions Feature failed while pricing; falling back to base rules."
        )

        return None


def _combine(base: DiscountOutcome, advanced, subtotal: Decimal) -> DiscountOutcome:
    """
    Decides between the base rule and an advanced one.

    Not stacking is the default, and it is the safe default: two rules that both
    apply in full are how a basket ends up discounted twice for the same reason.
    A Feature rule that is genuinely additive says so, and even then the total is
    capped at the basket — a discount larger than the goods is a refund, which is
    a different transaction entirely.
    """
    if advanced is None or advanced.discount_amount <= Decimal("0"):
        return base

    if not advanced.stacks:
        return _as_outcome(advanced) if advanced.discount_amount > base.discount_amount else base

    combined = min(base.discount_amount + advanced.discount_amount, subtotal)

    if base.discount_amount <= Decimal("0"):
        return _as_outcome(advanced, amount=combined)

    # Both applied. The order snapshots one row, so it records the pair by name
    # rather than pretending only one of them happened.
    return DiscountOutcome(
        promotion_id=base.promotion_id,
        coupon_id=base.coupon_id,
        promotion_name=f"{base.promotion_name} + {advanced.rule_name}",
        coupon_code=base.coupon_code,
        discount_type="Combined",
        discount_value=combined,
        discount_amount=combined,
    )


def _as_outcome(advanced, amount: Decimal | None = None) -> DiscountOutcome:
    """
    Turns the Feature's answer into an order snapshot.

    The conversion happens here rather than in the Feature, because the Feature
    may not import this module - so it returns plain data about its own rule and
    the base store decides what that means for an order.

    `promotion_id` stays None deliberately. It names a row in this app's table,
    and an advanced rule is not one; recording the Feature's id in it would make
    a snapshot point at the wrong table the moment anybody trusted it.
    """
    return DiscountOutcome(
        promotion_id=None,
        coupon_id=None,
        promotion_name=advanced.rule_name,
        coupon_code="",
        discount_type=advanced.rule_type,
        discount_value=advanced.discount_amount,
        discount_amount=amount if amount is not None else advanced.discount_amount,
    )


@transaction.atomic
def redeem(coupon_id: int, source_order_id: int) -> bool:
    """
    Records that a coupon was used on an order.

    Idempotent by constraint rather than by checking first: two concurrent
    checkouts both reading "not yet redeemed" is exactly how a limited campaign
    gets over-redeemed, and the database is the only place that race can be
    settled.

    Answers whether this call was the one that recorded it.
    """
    from django.db import IntegrityError

    from .models import CouponRedemption

    try:
        CouponRedemption.objects.create(coupon_id=coupon_id, source_order_id=source_order_id)
    except IntegrityError:
        return False

    return True
