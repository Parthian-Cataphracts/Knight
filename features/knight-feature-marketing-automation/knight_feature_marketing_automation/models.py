"""
Triggered email campaigns, and the record of every message they sent.

This Feature sends mail to real people on a timer, which makes it the first one
where the dangerous failure is not losing data — it is sending. Three tables
exist purely to stop that:

- **`Contact`** holds the address and *when consent was given*. No consent, no
  send. The Feature cannot see the shopper table, so the store registers
  contacts explicitly, which is the right way round: consent is a fact the store
  collected and must state, not something a marketing package should infer.
- **`Suppression`** is the list of people who must never be mailed again —
  unsubscribed, hard-bounced, or complained. Checked before every send. Ignoring
  it is illegal in most of the markets this sells into and gets the sending
  domain blocklisted in all of them.
- **`Send`** records every attempt, with the provider's message id where there
  is one. "Did the customer get the email" is the only question anybody asks
  afterwards.

A customer is an opaque **subject string**, the same one the rest of the
catalogue uses. Audiences come from `customer-segmentation`; triggers come from
`analytics-core`'s event stream. Neither is imported as models
([`feature-authoring.md`](../../../docs/feature-authoring.md)).
"""

from django.core.exceptions import ValidationError
from django.db import models
from django.utils import timezone


class Trigger(models.TextChoices):
    """
    What starts a campaign.

    A closed list. A rule language would be a query engine with a migration
    path, and these four are what the strategy asks for and what merchants ask
    for.
    """

    WELCOME = "Welcome", "A new customer"
    WIN_BACK = "WinBack", "A customer who has gone quiet"
    POST_PURCHASE = "PostPurchase", "After an order"
    ABANDONED_CART = "AbandonedCart", "A basket left behind"


#: Which analytics event each event-driven trigger waits for. The store emits
#: these; this Feature only reads them. Named here rather than configured,
#: because a campaign pointed at an event nobody emits is a campaign that
#: silently never runs, and the store's own docs can state these two names.
TRIGGER_EVENTS = {
    Trigger.POST_PURCHASE: "order.placed",
    Trigger.ABANDONED_CART: "cart.abandoned",
}

#: Which segment each audience-driven trigger draws from. Both are seeded by
#: `customer-segmentation`, which is why it is a hard dependency.
TRIGGER_SEGMENTS = {
    Trigger.WELCOME: "new-customers",
    Trigger.WIN_BACK: "dormant-customers",
}


class SendState(models.TextChoices):
    PENDING = "Pending", "Not yet attempted"
    SENT = "Sent", "Handed to the provider"
    FAILED = "Failed", "The provider refused it"
    SUPPRESSED = "Suppressed", "Not sent: the recipient must not be mailed"
    NO_CONTACT = "NoContact", "Not sent: no consented address"


class SuppressionReason(models.TextChoices):
    UNSUBSCRIBED = "Unsubscribed", "Asked not to be mailed"
    BOUNCED = "Bounced", "The address does not accept mail"
    COMPLAINED = "Complained", "Reported as spam"
    MANUAL = "Manual", "Suppressed by staff"


class Contact(models.Model):
    """
    Somebody the store may mail, and the moment they agreed to it.

    `consented_at` is not nullable and has no default. A contact row that exists
    without a consent timestamp would be a permission nobody granted, and the
    only safe way to make that impossible is to require it at the point the store
    registers the contact.
    """

    subject = models.CharField(max_length=200, unique=True)
    email = models.EmailField()

    consented_at = models.DateTimeField()

    # For a store that mails in more than one language. Not a locale object:
    # this Feature does not resolve it, it hands it to the template.
    locale = models.CharField(max_length=16, blank=True, default="")

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        db_table = "knight_marketing_contact"
        ordering = ("subject",)
        indexes = [models.Index(fields=["email"], name="knight_mkt_contact_email")]

    def __str__(self) -> str:
        return f"{self.subject} <{self.email}>"


class Suppression(models.Model):
    """
    An address that must never be mailed again.

    Keyed on the **email**, not the subject. Somebody who unsubscribes has
    withdrawn permission for that address, and a store that later registers the
    same address under a different customer id must not get a fresh start.

    Append-only in practice: removing a suppression is re-subscribing somebody
    who asked to be left alone, which is a deliberate act with a reason on it and
    not something a campaign run should ever do.
    """

    email = models.EmailField(unique=True)
    reason = models.CharField(max_length=20, choices=SuppressionReason)
    detail = models.CharField(max_length=250, blank=True, default="")
    created_at = models.DateTimeField(default=timezone.now)

    class Meta:
        db_table = "knight_marketing_suppression"
        ordering = ("-created_at",)

    def __str__(self) -> str:
        return f"{self.email} ({self.reason})"


class Campaign(models.Model):
    """
    One triggered message.

    `delay_hours` is the gap between the trigger and the send. It is the field
    that makes a campaign feel considered rather than automated: a win-back sent
    the moment somebody goes quiet is a robot, and one sent a day after an order
    asking how it was is a shop.
    """

    name = models.CharField(max_length=150)
    slug = models.SlugField(max_length=150, unique=True)
    trigger = models.CharField(max_length=20, choices=Trigger)

    is_active = models.BooleanField(default=False)

    subject = models.CharField(max_length=200)

    # A Django template string rather than a file. A campaign body is edited by
    # whoever runs the shop, and a file would mean a deploy to change a comma.
    body = models.TextField(max_length=8000)

    delay_hours = models.PositiveIntegerField(default=24)

    # Narrows the audience further. Empty means the trigger's own audience.
    segment_slug = models.SlugField(max_length=150, blank=True, default="")

    # A cap per run, so a misconfigured campaign cannot mail a whole customer
    # list in one sweep before anybody notices. Nothing about this is optional:
    # the first thing that goes wrong with automated mail is volume.
    maximum_per_run = models.PositiveIntegerField(default=200)

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    last_run_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        db_table = "knight_marketing_campaign"
        ordering = ("name",)

    def clean(self) -> None:
        super().clean()

        if not self.subject.strip():
            raise ValidationError({"subject": "A campaign needs a subject line."})

        if self.maximum_per_run == 0:
            # Zero would be a campaign that is active and sends nothing, which
            # reads as broken rather than as paused. `is_active` is how you pause.
            raise ValidationError(
                {"maximum_per_run": "Use is_active to pause a campaign, not a cap of zero."}
            )

    def __str__(self) -> str:
        return f"{self.name} ({self.trigger})"


class Send(models.Model):
    """
    One attempt to mail one person, and what became of it.

    Unique per campaign and subject, which is the whole safety property: a
    campaign cannot mail the same person twice however many times a run happens,
    and the database is the only place that can be guaranteed.

    The body is stored as it was sent. A template changes; what somebody was
    actually told does not, and a complaint about a message from March needs the
    March wording.
    """

    campaign = models.ForeignKey(Campaign, on_delete=models.CASCADE, related_name="sends")
    subject_ref = models.CharField(max_length=200)
    email = models.EmailField(blank=True, default="")

    state = models.CharField(max_length=16, choices=SendState, default=SendState.PENDING)

    subject_line = models.CharField(max_length=200, blank=True, default="")
    body = models.TextField(blank=True, default="")

    # What the provider called it, for tracing a delivery through their logs.
    # Blank for anything that never reached a provider.
    provider_message_id = models.CharField(max_length=250, blank=True, default="")
    error = models.CharField(max_length=500, blank=True, default="")

    created_at = models.DateTimeField(default=timezone.now)
    sent_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        db_table = "knight_marketing_send"
        ordering = ("-created_at", "-id")
        indexes = [
            models.Index(fields=["campaign", "state"], name="knight_mkt_send_state"),
            models.Index(fields=["subject_ref"], name="knight_mkt_send_subject"),
        ]
        constraints = [
            models.UniqueConstraint(
                fields=["campaign", "subject_ref"],
                name="knight_marketing_once_per_campaign_and_subject",
            ),
        ]

    @property
    def delivered(self) -> bool:
        return self.state == SendState.SENT

    def __str__(self) -> str:
        return f"{self.campaign_id} -> {self.subject_ref} ({self.state})"
