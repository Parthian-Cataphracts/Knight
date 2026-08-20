"""
Pricing a basket against the promotions a store has running.

This is the only part of the Feature the base store talks to, and it talks to it
through a function rather than a model: the store must keep working when the
Feature is not installed, so every call site has to be able to fall back. A
service that returned models would make that fallback awkward and tempt somebody
into importing one.
"""

from __future__ import annotations

from dataclasses import dataclass
from decimal import Decimal

from django.db import transaction
from django.utils import timezone

from .models import Coupon, Promotion, PromotionStatus


@dataclass(frozen=True)
class DiscountOutcome:
    """
    What pricing decided, in terms the base store can snapshot.

    Deliberately plain data with no model in it: `orders.OrderPromotion` copies
    these fields verbatim, and it has to stay meaningful after this Feature is
    uninstalled.
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


def price(subtotal: Decimal, *, coupon_code: str | None = None, at=None) -> DiscountOutcome:
    """
    Finds the best discount available for this basket.

    A presented code is honoured exclusively: a shopper who typed one expects
    that one to be used, and silently substituting a better automatic promotion
    would be a support call even though it saved them money.

    Returns `NOTHING` rather than raising when nothing applies. Not qualifying is
    an ordinary outcome of pricing, and callers that had to catch an exception
    for it would be worse for it.
    """
    moment = at or timezone.now()

    if coupon_code:
        return _from_coupon(subtotal, coupon_code, moment)

    return _best_automatic(subtotal, moment)


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
