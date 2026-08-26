"""
Points, tiers, and the ledger underneath them.

The first Feature in the catalogue that keeps a running balance, and every
decision here follows from one rule: **the ledger is the truth and the balance is
derived from it.** A stored balance beside a ledger is two sources of truth that
agree until the first crash, and the one a customer argues about is the one that
is wrong.

Points are earned in **lots**. A lot has an expiry and a remaining amount, and a
redemption consumes the oldest lots first. That is more machinery than a single
counter, and it is the only way to answer the two questions a loyalty programme
is actually asked: *how many of my points are about to expire*, and *which points
did that redemption use*. A single counter can answer neither.

A customer is an opaque **subject string**, the same one the store passes when it
records an order. This package cannot see the shopper table — a Feature may not
import store business code — and must not acquire a foreign key into it.
"""

from decimal import Decimal

from django.core.exceptions import ValidationError
from django.core.validators import MinValueValidator
from django.db import models
from django.utils import timezone


class TransactionKind(models.TextChoices):
    """
    Why the ledger moved.

    `ADJUST` exists because a support agent will eventually have to put points
    back, and the alternative to a named, audited transaction is somebody
    editing a balance directly.
    """

    EARN = "Earn", "Earned"
    REDEEM = "Redeem", "Redeemed"
    EXPIRE = "Expire", "Expired"
    ADJUST = "Adjust", "Adjusted by staff"


class Programme(models.Model):
    """
    The store's loyalty settings. Exactly one row, always.

    Loaded through `current()` so a store that has never opened the settings page
    still behaves like one with defaults, instead of failing on a missing row
    somewhere deep in a checkout.
    """

    id = models.PositiveSmallIntegerField(primary_key=True, default=1)

    is_active = models.BooleanField(default=True)

    # Points earned per unit of currency spent. A rate rather than a table:
    # every store expresses this differently and all of them can be written as
    # a multiplier.
    points_per_currency_unit = models.DecimalField(
        max_digits=8, decimal_places=4, default=Decimal("1"),
        validators=[MinValueValidator(Decimal("0"))],
    )

    # What a point is worth when it is spent. Kept separate from the earn rate
    # so a store can run a 10-points-per-pound programme where 100 points buys
    # a pound, which is the shape most of them have.
    currency_per_point = models.DecimalField(
        max_digits=10, decimal_places=4, default=Decimal("0.01"),
        validators=[MinValueValidator(Decimal("0"))],
    )

    # Zero means points never expire. Not null: "no expiry" is a number of
    # months, and a nullable column would make every caller handle both.
    expiry_months = models.PositiveSmallIntegerField(default=12)

    # A floor on redemption. Letting somebody spend three points costs more in
    # support than it returns in loyalty.
    minimum_redemption_points = models.PositiveIntegerField(default=100)

    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        db_table = "knight_loyalty_programme"

    @classmethod
    def current(cls) -> "Programme":
        return cls.objects.get_or_create(id=1)[0]

    def points_for(self, amount: Decimal) -> int:
        """
        Points earned by spending `amount`.

        Floored, not rounded. Rounding up hands out points nobody paid for, and
        at scale that is a liability on somebody's balance sheet.
        """
        if amount <= Decimal("0") or self.points_per_currency_unit <= Decimal("0"):
            return 0

        return int(amount * self.points_per_currency_unit)

    def value_of(self, points: int) -> Decimal:
        """What `points` are worth as money, rounded down to the minor unit."""
        if points <= 0:
            return Decimal("0")

        return (Decimal(points) * self.currency_per_point).quantize(Decimal("0.01"))

    def __str__(self) -> str:
        return "active" if self.is_active else "paused"


class Tier(models.Model):
    """
    A rung on the ladder, reached by lifetime points earned.

    Lifetime rather than current balance, deliberately. A customer who redeems
    their points has not become less loyal, and a tier that drops when somebody
    spends is a tier that teaches them not to.
    """

    name = models.CharField(max_length=100)
    slug = models.SlugField(max_length=100, unique=True)
    threshold_points = models.PositiveIntegerField(default=0)

    # Applied to everything earned while the customer is in this tier. 1 means
    # no bonus, which is the right value for the entry tier.
    earn_multiplier = models.DecimalField(
        max_digits=5, decimal_places=2, default=Decimal("1"),
        validators=[MinValueValidator(Decimal("0"))],
    )

    benefits = models.TextField(max_length=1000, blank=True, default="")

    class Meta:
        db_table = "knight_loyalty_tier"
        ordering = ("threshold_points", "id")
        constraints = [
            models.UniqueConstraint(
                fields=["threshold_points"], name="knight_loyalty_one_tier_per_threshold"
            ),
        ]

    def __str__(self) -> str:
        return f"{self.name} (from {self.threshold_points})"


class Account(models.Model):
    """
    One customer's membership.

    Carries no balance column. `lifetime_points` is not a balance — it only ever
    increases, it is what decides the tier, and it can be recomputed from the
    ledger, so it is a cache of something monotonic rather than of something
    that has to net out.
    """

    subject = models.CharField(max_length=200, unique=True)

    lifetime_points = models.PositiveIntegerField(default=0)
    tier = models.ForeignKey(Tier, null=True, blank=True, on_delete=models.SET_NULL)

    joined_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        db_table = "knight_loyalty_account"
        ordering = ("-lifetime_points", "subject")

    def __str__(self) -> str:
        return f"{self.subject}: {self.lifetime_points} lifetime"


class Transaction(models.Model):
    """
    One movement of points. Append-only; nothing here is ever updated except
    `points_remaining`, and only on an `EARN` row as later redemptions consume it.

    `points` is signed — positive for earning, negative for spending and
    expiring — so a balance is a sum rather than a case statement, and a mistake
    in the sign is visible in a query rather than hidden in code.
    """

    account = models.ForeignKey(Account, on_delete=models.CASCADE, related_name="transactions")
    kind = models.CharField(max_length=16, choices=TransactionKind)
    points = models.IntegerField()

    # Only meaningful on an EARN row: how much of that lot has not yet been
    # spent or expired. This is the one mutable field in the ledger, and it is
    # what makes oldest-first consumption possible without rewriting history.
    points_remaining = models.PositiveIntegerField(default=0)

    # Null on an EARN row that never expires, and on every other kind.
    expires_at = models.DateTimeField(null=True, blank=True)

    # The order this movement belongs to, as a plain id. A foreign key would be
    # a database-level coupling into the base store.
    source_order_id = models.BigIntegerField(null=True, blank=True)

    reason = models.CharField(max_length=250, blank=True, default="")
    created_at = models.DateTimeField(default=timezone.now)

    class Meta:
        db_table = "knight_loyalty_transaction"
        ordering = ("-created_at", "-id")
        indexes = [
            # Oldest unspent lot first: the query every redemption runs.
            models.Index(
                fields=["account", "kind", "expires_at"], name="knight_loyalty_lot_idx"
            ),
        ]
        constraints = [
            models.UniqueConstraint(
                fields=["account", "kind", "source_order_id"],
                condition=models.Q(source_order_id__isnull=False),
                name="knight_loyalty_once_per_order_and_kind",
            ),
            models.CheckConstraint(
                # An EARN that awards nothing is not an event, and a REDEEM that
                # takes nothing is a support ticket waiting to happen.
                condition=~models.Q(points=0),
                name="knight_loyalty_no_zero_movements",
            ),
        ]

    def clean(self) -> None:
        super().clean()

        if self.kind == TransactionKind.EARN and self.points <= 0:
            raise ValidationError({"points": "Earning must add points."})

        if self.kind in (TransactionKind.REDEEM, TransactionKind.EXPIRE) and self.points >= 0:
            raise ValidationError({"points": "Redeeming and expiring must remove points."})

    @property
    def is_live_lot(self) -> bool:
        """Whether this is an earn lot with points still on it."""
        return self.kind == TransactionKind.EARN and self.points_remaining > 0

    def __str__(self) -> str:
        return f"{self.kind} {self.points:+d} for {self.account_id}"
