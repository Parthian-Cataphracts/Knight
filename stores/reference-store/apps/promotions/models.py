"""
Coupons and discounts: how a store sells for less than list price.

Part of the base store. A shop that cannot issue a discount code is missing
something every competing platform includes, so withholding it would monetise a
deficiency rather than sophistication — which is the one thing the catalogue
strategy forbids ([`adr/0024`](../../../../docs/adr/0024-base-store-versus-optional-feature.md)).
The sophistication is still sold: buy X get Y, bundles and stacking rules are
the `advanced-promotions` Feature, and it owns its own tables rather than
extending these.

Nothing here imports an order. An order records the discount it received as a
snapshot, which is why `orders.OrderPromotion` copies a promotion's name, type
and value instead of pointing at this row. That was written so a receipt stayed
readable after the promotions Feature was uninstalled; it now also has to keep
a receipt readable after a rule moved from that Feature into this app, which is
the same property answering a second question.
"""

from decimal import Decimal

from django.core.exceptions import ValidationError
from django.core.validators import MinValueValidator
from django.db import models
from django.utils import timezone


class PromotionStatus(models.TextChoices):
    DRAFT = "Draft", "Draft"
    ACTIVE = "Active", "Active"
    ARCHIVED = "Archived", "Archived"


class DiscountType(models.TextChoices):
    PERCENTAGE = "Percentage", "Percentage"
    FIXED_AMOUNT = "FixedAmount", "Fixed amount"


class CouponStatus(models.TextChoices):
    ACTIVE = "Active", "Active"
    ARCHIVED = "Archived", "Archived"


def normalize_code(value: str | None) -> str:
    """
    Reduces a coupon code to what it is matched by.

    Shoppers type codes in whatever case and spacing they were given them in, so
    " ramadan20 " and "RAMADAN20" are the same coupon. Uniqueness is declared on
    the normalised value, which makes this the definition of sameness rather than
    a convenience beside it.
    """
    if value is None or not value.strip():
        raise ValidationError("A coupon needs a code.")

    return "".join(value.split()).upper()


class Promotion(models.Model):
    """
    A reason to charge less, and the limits on it.

    Both caps exist because they fail differently. `minimum_subtotal` stops a
    discount applying to a basket too small to be worth it; `maximum_discount_amount`
    stops a percentage discount on an unexpectedly large basket from costing the
    store more than it meant to offer. A percentage promotion without the second
    is the classic way a business loses money on a campaign.
    """

    name = models.CharField(max_length=200)
    description = models.TextField(max_length=1000, blank=True, default="")
    status = models.CharField(max_length=20, choices=PromotionStatus, default=PromotionStatus.DRAFT)

    discount_type = models.CharField(max_length=20, choices=DiscountType)
    discount_value = models.DecimalField(
        max_digits=12, decimal_places=2, validators=[MinValueValidator(Decimal("0"))]
    )

    minimum_subtotal = models.DecimalField(
        max_digits=12, decimal_places=2, null=True, blank=True,
        validators=[MinValueValidator(Decimal("0"))],
    )
    maximum_discount_amount = models.DecimalField(
        max_digits=12, decimal_places=2, null=True, blank=True,
        validators=[MinValueValidator(Decimal("0"))],
    )

    starts_at = models.DateTimeField(null=True, blank=True)
    ends_at = models.DateTimeField(null=True, blank=True)

    # Whether a shopper has to present a code. A promotion without one applies
    # to every qualifying basket, which is a very different commercial decision.
    requires_coupon = models.BooleanField(default=True)

    # Which promotion wins when several qualify. Highest first; ties broken by
    # id so the outcome is at least deterministic rather than whatever the
    # database returned that day.
    priority = models.IntegerField(default=0)

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    archived_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        ordering = ("-priority", "id")
        indexes = [models.Index(fields=["status", "-priority"])]

    def clean(self) -> None:
        super().clean()

        if self.ends_at and self.starts_at and self.ends_at <= self.starts_at:
            raise ValidationError({"ends_at": "A promotion cannot end before it starts."})

        if self.discount_type == DiscountType.PERCENTAGE and self.discount_value > Decimal("100"):
            # A discount over 100% would pay the shopper to order, which the
            # order aggregate then clamps to zero — so the mistake would be
            # invisible rather than refused.
            raise ValidationError({"discount_value": "A percentage discount cannot exceed 100."})

    def is_live(self, at=None) -> bool:
        """Whether this promotion may be applied right now."""
        moment = at or timezone.now()

        if self.status != PromotionStatus.ACTIVE:
            return False

        if self.starts_at and moment < self.starts_at:
            return False

        if self.ends_at and moment >= self.ends_at:
            return False

        return True

    def discount_for(self, subtotal: Decimal) -> Decimal:
        """
        What this promotion takes off the given subtotal.

        Returns zero rather than raising when the basket does not qualify: not
        qualifying is an ordinary outcome of pricing, not an error, and callers
        that had to catch an exception per promotion would be worse for it.

        Never returns more than the subtotal. A discount larger than the goods
        is a refund, which is a different transaction entirely.
        """
        if subtotal <= Decimal("0"):
            return Decimal("0")

        if self.minimum_subtotal is not None and subtotal < self.minimum_subtotal:
            return Decimal("0")

        if self.discount_type == DiscountType.PERCENTAGE:
            amount = (subtotal * self.discount_value / Decimal("100")).quantize(Decimal("0.01"))
        else:
            amount = self.discount_value

        if self.maximum_discount_amount is not None:
            amount = min(amount, self.maximum_discount_amount)

        return min(amount, subtotal)

    def archive(self) -> None:
        """
        Withdraws the promotion without deleting it.

        Redemptions point at its coupons, and a campaign whose results cannot be
        counted afterwards was not worth running.
        """
        self.status = PromotionStatus.ARCHIVED
        self.archived_at = timezone.now()

    def __str__(self) -> str:
        return self.name


class Coupon(models.Model):
    """
    A code that unlocks a promotion.

    A promotion may have several — one per channel, so a campaign can be
    measured by where its codes were used.
    """

    promotion = models.ForeignKey(Promotion, on_delete=models.CASCADE, related_name="coupons")
    code = models.CharField(max_length=64)
    normalized_code = models.CharField(max_length=64, unique=True, editable=False)
    status = models.CharField(max_length=20, choices=CouponStatus, default=CouponStatus.ACTIVE)

    # Null means unlimited. Zero would mean "usable no times", which is a
    # different thing, and conflating them is how a campaign silently never runs.
    usage_limit_total = models.PositiveIntegerField(null=True, blank=True)

    starts_at = models.DateTimeField(null=True, blank=True)
    ends_at = models.DateTimeField(null=True, blank=True)
    created_at = models.DateTimeField(auto_now_add=True)
    archived_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        ordering = ("code",)

    def save(self, *args, **kwargs):
        self.normalized_code = normalize_code(self.code)

        return super().save(*args, **kwargs)

    @property
    def times_redeemed(self) -> int:
        return self.redemptions.count()

    def is_redeemable(self, at=None) -> bool:
        """
        Whether this code can be used right now.

        Its own window narrows the promotion's rather than replacing it: a code
        cannot outlive the campaign it belongs to, which is what stops an
        expired promotion being revived by a coupon somebody forgot to archive.
        """
        moment = at or timezone.now()

        if self.status != CouponStatus.ACTIVE:
            return False

        if not self.promotion.is_live(moment):
            return False

        if self.starts_at and moment < self.starts_at:
            return False

        if self.ends_at and moment >= self.ends_at:
            return False

        if self.usage_limit_total is not None and self.times_redeemed >= self.usage_limit_total:
            return False

        return True

    def __str__(self) -> str:
        return self.code


class CouponRedemption(models.Model):
    """
    A record that a code was used on an order.

    The order is a plain id rather than a foreign key. It was that way when this
    lived in a Feature, because a database-level relationship to the base store
    would have been exactly the coupling the split avoids. Both tables are in the
    base store now and a foreign key would work — but changing it would rewrite
    the column on every store for no behaviour, and the looser shape is the one
    the redemption rows arriving from the old Feature already have.

    Unique per coupon and order, which is what makes redeeming idempotent — a
    retried checkout cannot count one use twice and exhaust a campaign early.
    """

    coupon = models.ForeignKey(Coupon, on_delete=models.CASCADE, related_name="redemptions")
    source_order_id = models.BigIntegerField()
    redeemed_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        ordering = ("-redeemed_at",)
        constraints = [
            models.UniqueConstraint(
                fields=["coupon", "source_order_id"],
                # Namespaced away from the Feature's constraint of the same
                # purpose. Both tables exist at once while a store is being
                # migrated off the Feature, and PostgreSQL will not hold two
                # constraints of one name — so the transition, not taste,
                # decides this.
                name="base_promotion_redemption_once_per_order",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.coupon.code} on order {self.source_order_id}"
