"""
Choosing who to mail, and mailing them once.

Two dependencies, both used the way the authoring guide requires — through
another Feature's **service surface**, never its models:

- `customer-segmentation` supplies audiences. Welcome draws from
  `new-customers`, win-back from `dormant-customers`, and any campaign may
  narrow itself to a segment.
- `analytics-core` supplies event triggers. Post-purchase waits for
  `order.placed`, abandoned cart for `cart.abandoned`. The store emits those;
  this Feature only reads them.

Both imports are deferred into the functions rather than done at module import,
so a store whose dependency is mid-upgrade answers one run badly instead of
failing to start.

The send path has one shape and it is worth stating: **decide, record, then
send.** The `Send` row is written before the provider is called, inside a
savepoint, and its unique constraint is what makes a campaign unable to mail the
same person twice. Sending first and recording after is how a crash between the
two turns into a duplicate message.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

from django.db import IntegrityError, transaction
from django.template import Context, Template
from django.utils import timezone as django_timezone

from . import config, providers
from .models import (
    Campaign,
    Contact,
    Send,
    SendState,
    Suppression,
    SuppressionReason,
    Trigger,
    TRIGGER_EVENTS,
    TRIGGER_SEGMENTS,
)

logger = logging.getLogger(__name__)


class DependencyUnavailable(RuntimeError):
    """
    Raised when a dependency this Feature cannot work without is missing.

    Explicit rather than an empty audience. A campaign run that quietly mailed
    nobody because segmentation was absent looks exactly like a store with no
    customers, and a merchant would conclude the wrong thing.
    """


@dataclass(frozen=True)
class RunReport:
    """What one campaign run did. Every number here is a count somebody asks for."""

    campaign: str
    considered: int = 0
    sent: int = 0
    suppressed: int = 0
    no_contact: int = 0
    already_sent: int = 0
    failed: int = 0

    @property
    def did_nothing(self) -> bool:
        return self.sent == 0


def _segmentation():
    """`customer-segmentation`'s service surface, checked before use."""
    try:
        from knight_feature_customer_segmentation import services as segmentation
    except ImportError as error:  # pragma: no cover - depends on the install
        raise DependencyUnavailable(
            "customer-segmentation is not installed on this store, and marketing-automation "
            "cannot choose an audience without it."
        ) from error

    if not hasattr(segmentation, "members_of"):
        raise DependencyUnavailable(
            "customer-segmentation on this store is too old: it has no members_of()."
        )

    return segmentation


def _analytics():
    """`analytics-core`'s service surface, for the event-driven triggers."""
    try:
        from knight_feature_analytics_core import services as analytics
    except ImportError as error:  # pragma: no cover
        raise DependencyUnavailable(
            "analytics-core is not installed on this store, and the event-driven triggers "
            "cannot fire without it."
        ) from error

    if not hasattr(analytics, "subjects_between"):
        raise DependencyUnavailable(
            "analytics-core on this store is older than 1.1.0 and cannot group events by subject."
        )

    return analytics


# --- Contacts and consent ---------------------------------------------------


def register_contact(subject: str, email: str, *, consented_at=None, locale: str = "") -> Contact:
    """
    Records that a customer may be mailed, and when they agreed.

    The store calls this, because consent is a fact the store collected. A
    marketing package that inferred consent from the existence of a customer
    would be inventing permission nobody gave.
    """
    if not email.strip():
        raise ValueError("A contact needs an email address.")

    contact, _ = Contact.objects.update_or_create(
        subject=subject,
        defaults={
            "email": email.strip().lower(),
            "consented_at": consented_at or django_timezone.now(),
            "locale": locale,
        },
    )

    return contact


def suppress(email: str, *, reason: str = SuppressionReason.UNSUBSCRIBED, detail: str = "") -> Suppression:
    """
    Adds an address to the list that must never be mailed again.

    Keyed on the address, so registering the same address under a different
    customer id does not give a store a fresh start with somebody who asked to
    be left alone.
    """
    record, _ = Suppression.objects.update_or_create(
        email=email.strip().lower(),
        defaults={"reason": reason, "detail": detail[:250]},
    )

    return record


def is_suppressed(email: str) -> bool:
    return Suppression.objects.filter(email=email.strip().lower()).exists()


# --- Audiences --------------------------------------------------------------


def audience_for(campaign: Campaign, *, at: datetime | None = None) -> list[str]:
    """
    The subjects a campaign should consider on this run.

    Not who to mail — who to *consider*. Suppression, consent and
    already-sent are decided per recipient in `run()`, because each of them is a
    different outcome that has to be counted separately.
    """
    moment = at or django_timezone.now()
    trigger = campaign.trigger

    if trigger in TRIGGER_SEGMENTS:
        subjects = [
            row["subject"]
            for row in _segmentation().members_of(TRIGGER_SEGMENTS[trigger], limit=500)
        ]
    elif trigger in TRIGGER_EVENTS:
        # The window opens at the delay and closes a day later. Bounded at both
        # ends deliberately: an open-ended window would re-consider every order
        # the store has ever taken on every run, and the unique constraint would
        # be the only thing standing between that and a mailing.
        window_end = moment - timedelta(hours=campaign.delay_hours)
        window_start = window_end - timedelta(days=1)

        subjects = [
            row.subject
            for row in _analytics().subjects_between(
                window_start, window_end, name=TRIGGER_EVENTS[trigger]
            )
        ]
    else:
        logger.error("Campaign '%s' has an unknown trigger '%s'.", campaign.slug, trigger)
        return []

    if campaign.segment_slug:
        # Narrowed rather than replaced: a merchant who set both meant the
        # intersection, which is the only reading that makes the second field
        # useful.
        allowed = {row["subject"] for row in _segmentation().members_of(campaign.segment_slug, limit=500)}
        subjects = [subject for subject in subjects if subject in allowed]

    # Deduplicated while preserving order, so a cap takes the earliest.
    return list(dict.fromkeys(subjects))[: campaign.maximum_per_run]


# --- Sending ----------------------------------------------------------------


def render(campaign: Campaign, *, subject_ref: str, contact: Contact | None) -> tuple[str, str]:
    """
    The subject line and body for one recipient.

    Rendered per recipient rather than once per campaign, because the whole
    point of a template is that it says the recipient's name. An unsubscribe URL
    is always in the context: a marketing message without one is a complaint.
    """
    context = Context(
        {
            "subject": subject_ref,
            "email": contact.email if contact else "",
            "locale": contact.locale if contact else "",
            "campaign": campaign.name,
            "unsubscribe_url": config.value("unsubscribe_url", ""),
        }
    )

    return (
        Template(campaign.subject).render(context).strip(),
        Template(campaign.body).render(context).strip(),
    )


@transaction.atomic
def run(campaign: Campaign, *, at: datetime | None = None, dry_run: bool = False) -> RunReport:
    """
    Runs one campaign once.

    Every recipient goes through the same four gates, in this order: already
    sent, suppressed, no consented contact, then send. The order matters —
    checking suppression before already-sent would count somebody who
    unsubscribed after receiving the message as suppressed on every future run,
    which makes the numbers lie.
    """
    moment = at or django_timezone.now()

    if not campaign.is_active:
        return RunReport(campaign=campaign.slug)

    subjects = audience_for(campaign, at=moment)
    provider = providers.current()
    from_email = str(config.value("from_email", "")) or None

    considered = len(subjects)
    sent = suppressed = no_contact = already = failed = 0

    already_sent = set(
        Send.objects.filter(campaign=campaign, subject_ref__in=subjects).values_list(
            "subject_ref", flat=True
        )
    )

    for subject_ref in subjects:
        if subject_ref in already_sent:
            already += 1
            continue

        contact = Contact.objects.filter(subject=subject_ref).first()

        if contact is None:
            no_contact += 1
            _record(campaign, subject_ref, "", SendState.NO_CONTACT, moment, dry_run=dry_run)
            continue

        if is_suppressed(contact.email):
            suppressed += 1
            _record(campaign, subject_ref, contact.email, SendState.SUPPRESSED, moment, dry_run=dry_run)
            continue

        subject_line, body = render(campaign, subject_ref=subject_ref, contact=contact)

        if dry_run:
            sent += 1
            continue

        record = _record(
            campaign,
            subject_ref,
            contact.email,
            SendState.PENDING,
            moment,
            subject_line=subject_line,
            body=body,
        )

        if record is None:
            # Another run got there first. The constraint settled it, which is
            # exactly what it is for.
            already += 1
            continue

        delivery = provider.send(
            to=contact.email, subject=subject_line, body=body, from_email=from_email or ""
        )

        if delivery.delivered:
            record.state = SendState.SENT
            record.sent_at = moment
            record.provider_message_id = delivery.message_id[:250]
            record.error = ""
            sent += 1
        else:
            record.state = SendState.FAILED
            record.error = delivery.detail[:500]
            failed += 1

        record.save(update_fields=["state", "sent_at", "provider_message_id", "error"])

    if not dry_run:
        campaign.last_run_at = moment
        campaign.save(update_fields=["last_run_at", "updated_at"])

    return RunReport(
        campaign=campaign.slug,
        considered=considered,
        sent=sent,
        suppressed=suppressed,
        no_contact=no_contact,
        already_sent=already,
        failed=failed,
    )


def _record(
    campaign: Campaign,
    subject_ref: str,
    email: str,
    state: str,
    moment: datetime,
    *,
    subject_line: str = "",
    body: str = "",
    dry_run: bool = False,
) -> Send | None:
    """
    Writes the send row, or None when one already existed.

    Its own savepoint: an IntegrityError marks the whole transaction broken on
    PostgreSQL, so without this the caller could not carry on to the next
    recipient — one racing duplicate would abandon the rest of the run.
    """
    if dry_run:
        return None

    try:
        with transaction.atomic():
            return Send.objects.create(
                campaign=campaign,
                subject_ref=subject_ref,
                email=email,
                state=state,
                subject_line=subject_line,
                body=body,
                created_at=moment,
            )
    except IntegrityError:
        return None


def run_all(*, at: datetime | None = None) -> list[RunReport]:
    """
    Every active campaign. The worker's entrypoint.

    One campaign failing does not stop the others: a broken template on a
    win-back must not cost the store its post-purchase mail.
    """
    reports: list[RunReport] = []

    for campaign in Campaign.objects.filter(is_active=True).order_by("slug"):
        try:
            reports.append(run(campaign, at=at))
        except Exception as error:  # noqa: BLE001 - one campaign must not stop the rest
            logger.exception("Campaign '%s' failed to run.", campaign.slug)
            reports.append(RunReport(campaign=campaign.slug, failed=1))
            del error

    return reports


# --- Reading ----------------------------------------------------------------


def history(campaign_slug: str, *, limit: int = 100) -> list[dict]:
    """What a campaign sent, newest first. Without the body: this is a summary."""
    rows = Send.objects.filter(campaign__slug=campaign_slug)[: max(1, min(limit, 500))]

    return [
        {
            "subject": row.subject_ref,
            "email": row.email,
            "state": row.state,
            "providerMessageId": row.provider_message_id,
            "error": row.error,
            "sentAt": row.sent_at.isoformat() if row.sent_at else None,
        }
        for row in rows
    ]


def summary() -> list[dict]:
    """Every campaign with what it has sent, for an overview screen."""
    from django.db.models import Count, Q

    rows = (
        Campaign.objects.annotate(
            total=Count("sends"),
            delivered=Count("sends", filter=Q(sends__state=SendState.SENT)),
            failures=Count("sends", filter=Q(sends__state=SendState.FAILED)),
        )
        .order_by("name")
        .values("name", "slug", "trigger", "is_active", "total", "delivered", "failures", "last_run_at")
    )

    return [
        {
            "name": row["name"],
            "slug": row["slug"],
            "trigger": row["trigger"],
            "active": row["is_active"],
            "sends": row["total"],
            "delivered": row["delivered"],
            "failures": row["failures"],
            "lastRunAt": row["last_run_at"].isoformat() if row["last_run_at"] else None,
        }
        for row in rows
    ]


def ensure_default_campaigns() -> int:
    """
    Creates the four campaigns the strategy names, **switched off**.

    Off is the whole point. Templates arrive as a starting draft that somebody
    has to read, edit and approve; a marketing Feature that installed and began
    mailing would be the worst default in this catalogue.
    """
    defaults = [
        (
            "Welcome",
            "welcome",
            Trigger.WELCOME,
            24,
            "Welcome to the shop",
            "Hello,\n\nThanks for your first order. If anything is not right, reply to this "
            "message and a person will read it.\n\n{% if unsubscribe_url %}To stop these "
            "emails: {{ unsubscribe_url }}{% endif %}",
        ),
        (
            "How was it?",
            "post-purchase",
            Trigger.POST_PURCHASE,
            72,
            "How was your order?",
            "Hello,\n\nYour order arrived a few days ago. If it was good we would love to "
            "hear it, and if it was not we would rather hear that.\n\n"
            "{% if unsubscribe_url %}To stop these emails: {{ unsubscribe_url }}{% endif %}",
        ),
        (
            "You left something",
            "abandoned-cart",
            Trigger.ABANDONED_CART,
            4,
            "You left something behind",
            "Hello,\n\nThere is still something in your basket. It will be there when you "
            "come back.\n\n{% if unsubscribe_url %}To stop these emails: {{ unsubscribe_url }}{% endif %}",
        ),
        (
            "We miss you",
            "win-back",
            Trigger.WIN_BACK,
            0,
            "It has been a while",
            "Hello,\n\nIt has been a while since your last order. Nothing to do — just "
            "saying hello.\n\n{% if unsubscribe_url %}To stop these emails: {{ unsubscribe_url }}{% endif %}",
        ),
    ]

    created = 0

    for name, slug, trigger, delay, subject, body in defaults:
        _, made = Campaign.objects.get_or_create(
            slug=slug,
            defaults={
                "name": name,
                "trigger": trigger,
                "delay_hours": delay,
                "subject": subject,
                "body": body,
                "is_active": False,
            },
        )

        if made:
            created += 1

    return created
