"""
Sending a transactional notification, and recording that it was sent.

One entry point. Callers say what happened and to whom; this renders it, records
it, and tries to deliver it.

Two decisions worth knowing about:

**Sending never raises into the caller.** A mail server being down must not fail
a checkout that has already taken money. The failure is recorded with its reason
and the caller is told it did not send, which is a fact it can act on or ignore.

**A notification about an order is sent once.** The uniqueness constraint on
(kind, order) is what makes that true under a retried checkout, rather than a
check that two concurrent requests both pass.
"""

from __future__ import annotations

from dataclasses import dataclass

from django.conf import settings
from django.core.mail import send_mail
from django.db import IntegrityError, transaction
from django.template.loader import render_to_string
from django.utils import timezone

from .models import Notification, NotificationKind, NotificationStatus

# Subjects live here rather than in the templates so that a subject line cannot
# pick up a stray newline from template whitespace, which some mail servers
# reject and others silently mangle into a header.
SUBJECTS = {
    NotificationKind.ORDER_CONFIRMATION: "We have your order",
    NotificationKind.PAYMENT_CONFIRMATION: "Payment received",
    NotificationKind.ORDER_CANCELLED: "Your order was cancelled",
    NotificationKind.ORDER_FULFILLED: "Your order is on its way",
    NotificationKind.PASSWORD_RESET: "Reset your password",
}


@dataclass(frozen=True)
class SendResult:
    notification: Notification | None
    sent: bool
    duplicate: bool = False


def notify(
    kind: str,
    *,
    recipient: str,
    context: dict | None = None,
    source_order_id: int | None = None,
) -> SendResult:
    """
    Renders, records and sends one notification.

    Returns what happened rather than raising. `duplicate` is not a failure: it
    means this notification had already been sent for this order, which is the
    correct outcome of a retried checkout and not something a caller should
    treat as an error.
    """
    subject = SUBJECTS.get(kind, "A message about your order")
    body = render_to_string(f"notifications/{_template_name(kind)}.txt", context or {}).strip()

    try:
        with transaction.atomic():
            notification = Notification.objects.create(
                kind=kind,
                recipient=recipient,
                subject=subject,
                body=body,
                source_order_id=source_order_id,
            )
    except IntegrityError:
        # Already sent for this order. Deliberately not re-sent: a shopper
        # getting two confirmations for one order reads as a double charge.
        return SendResult(
            notification=Notification.objects.filter(
                kind=kind, source_order_id=source_order_id
            ).first(),
            sent=False,
            duplicate=True,
        )

    try:
        send_mail(
            subject=subject,
            message=body,
            from_email=getattr(settings, "DEFAULT_FROM_EMAIL", "store@localhost"),
            recipient_list=[recipient],
            fail_silently=False,
        )
    except Exception as error:  # noqa: BLE001 - a mail failure must not fail the sale
        notification.status = NotificationStatus.FAILED
        notification.error = str(error)[:500]
        notification.save(update_fields=["status", "error"])

        return SendResult(notification=notification, sent=False)

    notification.status = NotificationStatus.SENT
    notification.sent_at = timezone.now()
    notification.save(update_fields=["status", "sent_at"])

    return SendResult(notification=notification, sent=True)


def _template_name(kind: str) -> str:
    """`OrderConfirmation` becomes `order_confirmation`."""
    out = []

    for index, character in enumerate(kind):
        if character.isupper() and index:
            out.append("_")

        out.append(character.lower())

    return "".join(out)


def unsent() -> list[Notification]:
    """
    Everything that failed to send, newest first.

    The list an operator needs after a mail outage: what did not arrive, so it
    can be dealt with deliberately rather than discovered through complaints.
    """
    return list(Notification.objects.filter(status=NotificationStatus.FAILED))
