"""
The subscription tables.

This is the first Feature in the catalogue whose worst failure is not losing
data. It is **taking money from somebody who did not owe it**, and every decision
below is made against that.

**A period is billed once, and the database is what says so.** Not the code, not
a lock, not an idempotency key a caller has to remember to pass: a unique index on
the period's number within its subscription, so two workers racing to bill the
same subscription both try to open period 7 and exactly one of them can. Every
other guard in this package can
be bypassed by a caller doing something unexpected; that one cannot be bypassed
by anybody, which is the only standard worth holding a charge to. A cron that
fires twice, a webhook delivered twice, an operator running the worker by hand
during an incident — all three end at the same constraint.

**Attempts are a ledger, and the ledger is the truth.** The same rule the two
money Features of phase 14 and the stock ledger of phase 16 are built on. A
subscription has no `times_charged` counter. What a customer has paid is the sum
of what succeeded, and "why was I charged twice in March" is a query rather than
an apology.

**A period exists before it is paid, and outlives failing to be paid.** Rows are
created for the period rather than for the charge, so a failed payment is a
period in a state rather than an absence. An absence cannot be retried, reported
on, or explained.

**No card data, ever.** `payment_method_reference` is a token a provider gave the
store for a payment method it holds. Nothing in this package is, or may become, a
place a card number could be stored — there is no field it would fit in and that
is deliberate. A Feature that could hold one would put every store that installs
it into a compliance regime it did not choose.
"""

from decimal import Decimal

from django.core.validators import MinValueValidator
from django.db import models

#: Money, everywhere in this package. Two places, because that is what a price
#: is; fourteen digits, because a currency with small units needs the room.
MONEY = {"max_digits": 14, "decimal_places": 2}

#: The location a subscription belongs to, for a merchant with more than one
#: branch. The same bare code `advanced-inventory` and `restaurant-operations`
#: carry and `multi-location` names, present from 1.0 for the same reason: a
#: Feature owns only its own tables, so adding it later would be a migration over
#: every subscription a merchant had ever taken.
DEFAULT_LOCATION = ""


class Interval(models.TextChoices):
    """
    How often a subscription renews.

    A closed list. "Every 45 days" is expressible as 45 daily intervals and is
    not worth a second field on every row; what a merchant actually sells is
    weekly, fortnightly, monthly and yearly, and the fortnight is two weeks.
    """

    DAILY = "daily", "Every day"
    WEEKLY = "weekly", "Every week"
    MONTHLY = "monthly", "Every month"
    YEARLY = "yearly", "Every year"


class SubscriptionState(models.TextChoices):
    """
    Where a subscription is.

    `past_due` and `unpaid` are separate states and the difference matters
    commercially: `past_due` is a payment that failed and will be tried again,
    and `unpaid` is one that has stopped being tried. Collapsing them would mean
    a merchant could not tell "we are chasing this" from "we have given up", and
    a shopper could not be told either.
    """

    PENDING = "pending", "Not started yet"
    ACTIVE = "active", "Billing normally"
    PAST_DUE = "past-due", "A payment failed and will be retried"
    UNPAID = "unpaid", "Retries are exhausted"
    PAUSED = "paused", "Paused by the shopper or the merchant"
    CANCELLED = "cancelled", "Ended by somebody"
    ENDED = "ended", "Ran to its natural end"


#: What each state may become. Read by the aggregate rather than scattered
#: through it, so the whole rule is visible in one place and a new state cannot
#: be added without deciding what it follows — the same arrangement the store's
#: own order aggregate and `restaurant-operations` both use.
#:
#: `unpaid` may return to `active`: a shopper who fixes their card is a shopper
#: the merchant wants back, and a state machine with no way to say so is one
#: staff work around by creating a second subscription.
ALLOWED_TRANSITIONS: dict[str, set[str]] = {
    SubscriptionState.PENDING: {SubscriptionState.ACTIVE, SubscriptionState.CANCELLED},
    SubscriptionState.ACTIVE: {
        SubscriptionState.PAST_DUE,
        SubscriptionState.PAUSED,
        SubscriptionState.CANCELLED,
        SubscriptionState.ENDED,
    },
    SubscriptionState.PAST_DUE: {
        SubscriptionState.ACTIVE,
        SubscriptionState.UNPAID,
        SubscriptionState.PAUSED,
        SubscriptionState.CANCELLED,
    },
    SubscriptionState.UNPAID: {SubscriptionState.ACTIVE, SubscriptionState.CANCELLED},
    SubscriptionState.PAUSED: {SubscriptionState.ACTIVE, SubscriptionState.CANCELLED},
    SubscriptionState.CANCELLED: set(),
    SubscriptionState.ENDED: set(),
}

#: The states in which a subscription is entitled to be billed. Named once,
#: because "which subscriptions do we charge tonight" appearing twice with two
#: different answers is how a paused customer gets charged.
BILLABLE = {SubscriptionState.ACTIVE, SubscriptionState.PAST_DUE}


class Subscription(models.Model):
    """
    One standing agreement to be charged and sent something, repeatedly.

    Everything about the shopper is a snapshot, exactly as the store's own orders
    snapshot theirs: a `source_shopper_id` kept only for tracing, and the name
    and email as they were. A shopper renaming themselves, or exercising a right
    to be forgotten, must not silently rewrite who a past charge belonged to.
    """

    reference = models.CharField(max_length=64, unique=True)

    source_shopper_id = models.BigIntegerField(null=True, blank=True, db_index=True)
    display_name = models.CharField(max_length=200, blank=True, default="")
    email = models.EmailField(blank=True, default="")

    state = models.CharField(max_length=20, choices=SubscriptionState, default=SubscriptionState.PENDING)
    location = models.CharField(max_length=40, blank=True, default=DEFAULT_LOCATION)

    interval = models.CharField(max_length=12, choices=Interval, default=Interval.MONTHLY)

    #: How many intervals make one period. Three monthly intervals is a quarterly
    #: subscription, and expressing it this way means "quarterly" never has to
    #: become a fifth interval with its own arithmetic.
    interval_count = models.PositiveSmallIntegerField(default=1)

    currency = models.CharField(max_length=3, default="IRR")

    #: What one period costs, stored rather than summed from the lines on read.
    #: A price that recalculated itself when a product was repriced would
    #: retroactively change what a shopper agreed to pay — which is the
    #: difference between an agreement and an estimate, and the same call the
    #: store's own orders make about their totals.
    amount = models.DecimalField(**MONEY, validators=[MinValueValidator(Decimal("0"))])

    #: The provider that will take the money, and its token for the payment
    #: method. **Never card data**: this is an opaque string a provider gave the
    #: store, and there is deliberately no field in this package a card number
    #: would fit in.
    provider = models.CharField(max_length=40, default="manual")
    payment_method_reference = models.CharField(max_length=200, blank=True, default="")

    started_on = models.DateField(null=True, blank=True)

    #: When the next period becomes billable. **This is the clock**: due-ness is
    #: this field against the wall clock, never "has the worker run". A store
    #: whose cron is broken bills late; a store whose billing depended on the
    #: cron having run would bill wrongly.
    next_run_at = models.DateTimeField(null=True, blank=True, db_index=True)

    #: Where the sequence is up to. The next period takes this plus one, under a
    #: row lock, which is what makes two workers unable to open the same period.
    last_period = models.PositiveIntegerField(default=0)

    #: A subscription that ends on its own — twelve boxes, then stop. Null means
    #: it runs until somebody stops it.
    ends_on = models.DateField(null=True, blank=True)
    periods_limit = models.PositiveIntegerField(null=True, blank=True)

    paused_at = models.DateTimeField(null=True, blank=True)
    cancelled_at = models.DateTimeField(null=True, blank=True)
    cancellation_reason = models.CharField(max_length=500, blank=True, default="")
    ended_at = models.DateTimeField(null=True, blank=True)

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    #: Incremented on every transition, so a caller that read a subscription,
    #: decided something and wrote back can tell that nothing moved underneath.
    version = models.PositiveIntegerField(default=1)

    class Meta:
        db_table = "knight_subscriptions_subscription"
        ordering = ("-created_at",)
        indexes = [
            # The query the billing worker makes, and the only one that runs on a
            # timer against the whole table.
            models.Index(fields=["state", "next_run_at"], name="knight_sub_due"),
            models.Index(fields=["source_shopper_id"], name="knight_sub_shopper"),
        ]
        constraints = [
            models.CheckConstraint(
                condition=models.Q(amount__gte=Decimal("0")),
                name="knight_sub_amount_not_negative",
            ),
            models.CheckConstraint(
                condition=models.Q(interval_count__gt=0),
                name="knight_sub_interval_count_is_positive",
            ),
        ]

    @property
    def is_terminal(self) -> bool:
        return self.state in {SubscriptionState.CANCELLED, SubscriptionState.ENDED}

    @property
    def is_billable(self) -> bool:
        return self.state in BILLABLE

    def __str__(self) -> str:
        return f"{self.reference} ({self.state})"


class SubscriptionLine(models.Model):
    """
    What arrives each period.

    Priced here as well as on the subscription, because a merchant needs to be
    able to answer "what am I paying for" and a total cannot. `source_*` ids are
    plain integers with no foreign key — the arrangement every Feature in this
    catalogue uses, so that an archived product cannot be resurrected by a
    cascade and a line stays readable if the product row is genuinely gone.
    """

    subscription = models.ForeignKey(Subscription, on_delete=models.CASCADE, related_name="lines")

    source_product_id = models.BigIntegerField(null=True, blank=True)
    source_variant_id = models.BigIntegerField(null=True, blank=True)
    sku = models.CharField(max_length=100, blank=True, default="")
    name = models.CharField(max_length=200)

    quantity = models.PositiveIntegerField(default=1)
    unit_price = models.DecimalField(**MONEY, default=Decimal("0"))
    display_order = models.PositiveSmallIntegerField(default=0)

    class Meta:
        db_table = "knight_subscriptions_line"
        ordering = ("display_order", "id")
        constraints = [
            models.CheckConstraint(
                condition=models.Q(quantity__gt=0),
                name="knight_sub_line_has_quantity",
            ),
        ]

    @property
    def line_total(self) -> Decimal:
        return self.unit_price * self.quantity

    def __str__(self) -> str:
        return f"{self.quantity} × {self.name}"


class PeriodState(models.TextChoices):
    PENDING = "pending", "Not charged yet"
    PAID = "paid", "Charged successfully"
    FAILED = "failed", "The charge failed"
    SKIPPED = "skipped", "Deliberately not charged"
    REFUNDED = "refunded", "Charged and given back"


class BillingPeriod(models.Model):
    """
    One stretch of time a shopper owes for.

    **The row that stops double-billing.** A period is opened before it is
    charged, numbered in a sequence per subscription, and unique on that number:
    two workers racing to bill the same subscription both try to open period 7
    and exactly one of them succeeds. The other gets an IntegrityError, which is
    the correct outcome and the only one that does not depend on a caller
    behaving well.

    Kept forever in practice. What a shopper was charged and when is the answer
    to a chargeback, and a chargeback arrives months later.
    """

    subscription = models.ForeignKey(Subscription, on_delete=models.CASCADE, related_name="periods")

    #: 1, 2, 3… per subscription. The number a merchant and a shopper can both
    #: count in, and the number the unique constraint is on.
    sequence = models.PositiveIntegerField()

    starts_on = models.DateField()
    ends_on = models.DateField()

    #: What this period cost, snapshotted from the subscription when the period
    #: was opened. A price rise applies to the periods after it and never to one
    #: already open, which is the difference between a price change and a
    #: retrospective one.
    currency = models.CharField(max_length=3, default="IRR")
    amount = models.DecimalField(**MONEY, validators=[MinValueValidator(Decimal("0"))])

    state = models.CharField(max_length=12, choices=PeriodState, default=PeriodState.PENDING)
    settled_at = models.DateTimeField(null=True, blank=True)

    #: How many times a charge has been attempted for this period.
    #:
    #: Named `attempt_count` rather than `attempts` because `attempts` is what
    #: the ledger of attempts is called, and a counter that shadowed the rows it
    #: counts would be exactly the confusion this Feature is built to avoid.
    #: Denormalised deliberately and only as a convenience for the retry query -
    #: the rows are still the truth.
    attempt_count = models.PositiveSmallIntegerField(default=0)

    #: When the next retry becomes due, or null when none is scheduled. The same
    #: kind of clock as `Subscription.next_run_at` and read the same way: by time,
    #: never by "has the retry job run".
    retry_at = models.DateTimeField(null=True, blank=True)

    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_subscriptions_period"
        ordering = ("subscription", "sequence")
        indexes = [
            models.Index(fields=["state", "retry_at"], name="knight_sub_period_retry"),
        ]
        constraints = [
            # The constraint this whole Feature is arranged around. Not a lock,
            # not an idempotency key a caller has to remember: two attempts to
            # open the same period end with one row and one error.
            models.UniqueConstraint(
                fields=["subscription", "sequence"],
                name="knight_sub_one_period_per_sequence",
            ),
            models.CheckConstraint(
                condition=models.Q(ends_on__gte=models.F("starts_on")),
                name="knight_sub_period_ends_after_it_starts",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.subscription_id} #{self.sequence} ({self.state})"


class AttemptOutcome(models.TextChoices):
    SUCCEEDED = "succeeded", "The money moved"
    FAILED = "failed", "The provider declined it"
    REFUSED = "refused", "This Feature would not try"


class BillingAttempt(models.Model):
    """
    One attempt to take money, and what came back.

    **Append-only.** Nothing in this package updates or deletes an attempt, and
    that is what makes "why was I charged twice in March" a query. A refund is
    another row and another period state; it is not the editing away of a charge
    that really did happen — the same argument `advanced-inventory` makes about a
    returned item and `gift-cards` makes about a spent balance.

    `refused` is its own outcome and not a kind of failure. A failure is the
    provider saying no; a refusal is this Feature declining to ask, because the
    period was already paid, the subscription was paused, or no provider was
    configured. Reporting those together would make a merchant think their
    payments were being declined.
    """

    period = models.ForeignKey(BillingPeriod, on_delete=models.CASCADE, related_name="attempts")

    #: 1, 2, 3… within the period.
    attempt = models.PositiveSmallIntegerField(default=1)

    outcome = models.CharField(max_length=12, choices=AttemptOutcome)
    provider = models.CharField(max_length=40, blank=True, default="")

    #: What the provider called this attempt, so a merchant can find it in the
    #: provider's own dashboard during a dispute.
    provider_reference = models.CharField(max_length=200, blank=True, default="")

    #: The provider's own words, or this Feature's reason for refusing. Kept
    #: short and never trusted into a page: it is somebody else's string.
    detail = models.CharField(max_length=500, blank=True, default="")

    amount = models.DecimalField(**MONEY, default=Decimal("0"))
    occurred_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_subscriptions_attempt"
        ordering = ("occurred_at", "id")
        indexes = [
            models.Index(fields=["period", "occurred_at"], name="knight_sub_attempt_period"),
            models.Index(fields=["outcome"], name="knight_sub_attempt_outcome"),
        ]

    def __str__(self) -> str:
        return f"{self.period_id} attempt {self.attempt}: {self.outcome}"


class SubscriptionEvent(models.Model):
    """
    Every state a subscription has held, and who moved it.

    Append-only and written by the aggregate rather than by callers, so a
    subscription cannot change state without leaving a trace. On a Feature that
    takes money this is not diagnostics — it is the answer to "I never agreed to
    that", and it needs to exist before anybody asks.
    """

    subscription = models.ForeignKey(Subscription, on_delete=models.CASCADE, related_name="events")
    from_state = models.CharField(max_length=20, blank=True, default="")
    to_state = models.CharField(max_length=20)

    #: Free text rather than a user id: a subscription is paused by a shopper on
    #: a storefront, by staff on a phone, or by this Feature's own worker, and
    #: only one of those three is an account.
    actor = models.CharField(max_length=200, blank=True, default="")
    reason = models.CharField(max_length=500, blank=True, default="")
    occurred_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_subscriptions_event"
        ordering = ("occurred_at", "id")
        indexes = [
            models.Index(fields=["subscription", "occurred_at"], name="knight_sub_event"),
        ]

    def __str__(self) -> str:
        return f"{self.from_state or '—'} → {self.to_state}"


class SubscriptionOrder(models.Model):
    """
    The order a paid period turned into, once the store has made one.

    The seam with the store, and the same one `restaurant-operations` uses for
    kitchen tickets: this Feature may not create an order, because orders are the
    store's and a Feature that wrote them would be a Feature the store could not
    uninstall. So the period is marked as owing an order, the store's own command
    creates it, and the number comes back here.

    One order per period, by constraint. A store command run twice must not send
    a shopper two boxes.
    """

    period = models.OneToOneField(BillingPeriod, on_delete=models.CASCADE, related_name="order")
    source_order_number = models.BigIntegerField(unique=True)
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_subscriptions_order"
        ordering = ("-created_at",)

    def __str__(self) -> str:
        return f"period {self.period_id} → order {self.source_order_number}"
