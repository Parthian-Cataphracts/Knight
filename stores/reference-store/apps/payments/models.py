"""
Recording that an order was paid for.

Ported from the frozen `Payment` module. What is base is the *record* — that
money arrived, how much, by what method, and when. Provider integrations are
not: a store reconciling bank transfers by hand is a real business, and the base
image should serve it ([`adr/0024`](../../../../docs/adr/0024-base-store-versus-optional-feature.md)).

A payment is separate from its attempts on purpose. "This order is paid" is one
fact with one lifecycle; "the card was declined twice and then worked" is a
history, and collapsing the two loses the reason a shopper is annoyed.
"""

from decimal import Decimal

from django.core.exceptions import ValidationError
from django.core.validators import MinValueValidator
from django.db import models
from django.utils import timezone


class PaymentMethod(models.TextChoices):
    ONLINE = "Online", "Online"
    ON_FULFILLMENT = "PayOnFulfillment", "Pay on fulfilment"


class PaymentStatus(models.TextChoices):
    PENDING = "Pending", "Pending"
    PROCESSING = "Processing", "Processing"
    SUCCEEDED = "Succeeded", "Succeeded"
    FAILED = "Failed", "Failed"
    CANCELLED = "Cancelled", "Cancelled"


class AttemptStatus(models.TextChoices):
    CREATED = "Created", "Created"
    PROCESSING = "Processing", "Processing"
    SUCCEEDED = "Succeeded", "Succeeded"
    FAILED = "Failed", "Failed"
    CANCELLED = "Cancelled", "Cancelled"


#: What a payment may become. Succeeded is terminal in both directions: a
#: payment that succeeded and then "failed" is a refund, which is a different
#: transaction with its own record, not a status change on this one.
ALLOWED_TRANSITIONS: dict[str, set[str]] = {
    PaymentStatus.PENDING: {PaymentStatus.PROCESSING, PaymentStatus.SUCCEEDED, PaymentStatus.CANCELLED},
    PaymentStatus.PROCESSING: {PaymentStatus.SUCCEEDED, PaymentStatus.FAILED, PaymentStatus.CANCELLED},
    PaymentStatus.FAILED: {PaymentStatus.PROCESSING},
    PaymentStatus.SUCCEEDED: set(),
    PaymentStatus.CANCELLED: set(),
}


class Payment(models.Model):
    """
    What is owed on one order, and whether it has been settled.

    The order is referenced by id rather than by foreign key for the same reason
    order lines reference products that way: this record is evidence, and
    evidence should not disappear because something it points at was removed.
    """

    source_order_id = models.BigIntegerField(unique=True)
    order_number = models.BigIntegerField()

    amount = models.DecimalField(
        max_digits=14, decimal_places=2, validators=[MinValueValidator(Decimal("0"))]
    )
    currency = models.CharField(max_length=3, default="IRR")
    method = models.CharField(max_length=20, choices=PaymentMethod)
    status = models.CharField(max_length=20, choices=PaymentStatus, default=PaymentStatus.PENDING)

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    succeeded_at = models.DateTimeField(null=True, blank=True)
    failed_at = models.DateTimeField(null=True, blank=True)
    cancelled_at = models.DateTimeField(null=True, blank=True)

    version = models.PositiveIntegerField(default=1)

    class Meta:
        ordering = ("-created_at",)
        indexes = [models.Index(fields=["status", "-created_at"])]

    @property
    def is_settled(self) -> bool:
        return self.status == PaymentStatus.SUCCEEDED

    def transition_to(self, target: str, *, actor: str = "", reason: str = "") -> "PaymentStatusHistory":
        """
        Moves the payment on, and records the move.

        Refusing an illegal transition here rather than trusting callers is what
        stops a retried webhook from marking a settled payment as failed — which
        is the shape of the bug that ends with a shopper being charged and told
        they were not.
        """
        if target not in ALLOWED_TRANSITIONS[self.status]:
            raise ValidationError(f"A payment that is {self.status} cannot become {target}.")

        previous = self.status
        now = timezone.now()

        self.status = target
        self.version += 1

        if target == PaymentStatus.SUCCEEDED:
            self.succeeded_at = now
        elif target == PaymentStatus.FAILED:
            self.failed_at = now
        elif target == PaymentStatus.CANCELLED:
            self.cancelled_at = now

        self.save(
            update_fields=[
                "status", "version", "succeeded_at", "failed_at", "cancelled_at", "updated_at",
            ]
        )

        return PaymentStatusHistory.objects.create(
            payment=self,
            from_status=previous,
            to_status=target,
            actor=actor,
            reason=reason.strip(),
        )

    def start_attempt(self, *, provider_key: str = "") -> "PaymentAttempt":
        """
        Opens a new attempt.

        Numbered from the attempts already recorded, so the sequence a shopper
        or an operator reads matches what happened.
        """
        number = self.attempts.count() + 1

        return PaymentAttempt.objects.create(
            payment=self,
            attempt_number=number,
            provider_key=provider_key,
        )

    def __str__(self) -> str:
        return f"#{self.order_number}: {self.amount} {self.currency} ({self.status})"


class PaymentAttempt(models.Model):
    """
    One try at collecting the money.

    Kept even when it failed, and especially then: two declines followed by a
    success is the answer to "why does my statement show three transactions".
    """

    payment = models.ForeignKey(Payment, on_delete=models.CASCADE, related_name="attempts")
    attempt_number = models.PositiveIntegerField()
    status = models.CharField(max_length=20, choices=AttemptStatus, default=AttemptStatus.CREATED)

    # Which integration handled it. Blank for a payment taken at the counter,
    # which is the base store's normal case.
    provider_key = models.CharField(max_length=100, blank=True, default="")
    provider_reference = models.CharField(max_length=200, blank=True, default="")

    created_at = models.DateTimeField(auto_now_add=True)
    started_at = models.DateTimeField(null=True, blank=True)
    completed_at = models.DateTimeField(null=True, blank=True)

    failure_code = models.CharField(max_length=100, blank=True, default="")
    failure_message = models.CharField(max_length=500, blank=True, default="")

    class Meta:
        ordering = ("attempt_number",)
        constraints = [
            models.UniqueConstraint(
                fields=["payment", "attempt_number"],
                name="payments_attempt_number_unique",
            ),
        ]

    def succeed(self, *, reference: str = "") -> None:
        self.status = AttemptStatus.SUCCEEDED
        self.provider_reference = reference
        self.completed_at = timezone.now()
        self.save(update_fields=["status", "provider_reference", "completed_at"])

    def fail(self, *, code: str = "", message: str = "") -> None:
        self.status = AttemptStatus.FAILED
        self.failure_code = code
        self.failure_message = message[:500]
        self.completed_at = timezone.now()
        self.save(update_fields=["status", "failure_code", "failure_message", "completed_at"])

    def __str__(self) -> str:
        return f"attempt {self.attempt_number} ({self.status})"


class PaymentStatusHistory(models.Model):
    """Append-only record of how a payment reached its current state."""

    payment = models.ForeignKey(Payment, on_delete=models.CASCADE, related_name="history")
    from_status = models.CharField(max_length=20, choices=PaymentStatus, blank=True, default="")
    to_status = models.CharField(max_length=20, choices=PaymentStatus)
    changed_at = models.DateTimeField(auto_now_add=True)
    actor = models.CharField(max_length=200, blank=True, default="")
    reason = models.CharField(max_length=500, blank=True, default="")

    class Meta:
        ordering = ("changed_at", "id")
        verbose_name_plural = "payment status history"

    def __str__(self) -> str:
        return f"{self.from_status or '—'} → {self.to_status}"
