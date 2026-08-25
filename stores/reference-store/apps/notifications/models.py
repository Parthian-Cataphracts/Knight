"""
What the store told a shopper, and whether it arrived.

Part of the base store. A shop that cannot tell somebody their order was
received is broken rather than plainer, which is the test
[`adr/0024`](../../../../docs/adr/0024-base-store-versus-optional-feature.md)
applies. Marketing automation — campaigns, sequences, segments — is a Feature and
is a different thing entirely: this app only ever sends because something
happened to an order or an account.

Every send is recorded, including the failures. "Did the customer get the email"
is the first question support asks and the one a store with no record cannot
answer; a mail server that accepted nothing all afternoon should be visible here
rather than inferred from complaints.

This app is a leaf. It never imports orders, payments or shoppers — callers hand
it a recipient and a context, and it renders and sends. Anything else would make
the notification layer a place where the order lifecycle partly lives.
"""

from django.db import models


class NotificationKind(models.TextChoices):
    ORDER_CONFIRMATION = "OrderConfirmation", "Order confirmation"
    PAYMENT_CONFIRMATION = "PaymentConfirmation", "Payment confirmation"
    ORDER_CANCELLED = "OrderCancelled", "Order cancelled"
    ORDER_FULFILLED = "OrderFulfilled", "Order fulfilled"
    PASSWORD_RESET = "PasswordReset", "Password reset"


class NotificationStatus(models.TextChoices):
    PENDING = "Pending", "Pending"
    SENT = "Sent", "Sent"
    FAILED = "Failed", "Failed"


class Notification(models.Model):
    """
    One message, and what became of it.

    The body is stored as it was sent rather than re-rendered on demand. A
    template changes; what a shopper was actually told does not, and a support
    conversation about an order from March needs the March wording.
    """

    kind = models.CharField(max_length=32, choices=NotificationKind)
    recipient = models.EmailField()
    subject = models.CharField(max_length=250)
    body = models.TextField()

    status = models.CharField(
        max_length=16, choices=NotificationStatus, default=NotificationStatus.PENDING
    )

    # Why it failed, in the words the mail library used. Truncated rather than
    # unbounded: this is a breadcrumb for an operator, not a place to keep a
    # stack trace.
    error = models.CharField(max_length=500, blank=True, default="")

    # The order this is about, as a plain id. A foreign key would make this app
    # depend on orders, and a notification outliving the thing it announced is
    # ordinary rather than a problem to design against.
    source_order_id = models.BigIntegerField(null=True, blank=True)

    created_at = models.DateTimeField(auto_now_add=True)
    sent_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        ordering = ("-created_at", "-id")
        indexes = [models.Index(fields=["status", "-created_at"])]
        constraints = [
            models.UniqueConstraint(
                fields=["kind", "source_order_id"],
                condition=models.Q(source_order_id__isnull=False),
                name="notification_once_per_order_and_kind",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.kind} to {self.recipient} ({self.status})"
