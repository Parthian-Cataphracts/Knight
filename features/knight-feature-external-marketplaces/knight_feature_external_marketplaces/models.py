"""
The integration tables.

This is the last Feature in the catalogue and the one with the most third-party
surface, which is why it was scheduled last. Everything it does happens at a
boundary the store does not control: a marketplace that changes its payload
shape, a POS that goes down for an afternoon, an accounting system that delivers
the same webhook four times because its own retry logic could not see our 200.

So the design is one sentence: **everything that crosses the boundary is a row.**

Not a call, not a callback, not a best effort — a row, with a state, an attempt
count and a payload, written before anything is attempted and kept after it
succeeds. Which gives the four properties that make an integration supportable:

- **"did that order reach the POS" is a query**, answerable in a support ticket
  rather than by reading logs and inferring;
- **a redelivery is free.** The counterpart's own event id is unique per
  connection, so the fourth copy of a webhook is a database refusal and not four
  orders;
- **a failure is retried on a schedule and then stops**, in a state that carries
  everything needed to replay it by hand. A queue with no ceiling is a queue that
  hammers a partner who is already having a bad day;
- **reconciliation compares and reports; it never quietly fixes.** A difference
  between what this store thinks and what a marketplace thinks is a thing a human
  decides about, because the two sides disagreeing about an order usually means
  one of them is right and it is not always us.

**Credentials live here, and that is stated rather than hidden.** A per-connection
OAuth token cannot come from KNIGHT's configuration channel, because there is one
per marketplace account and they are refreshed while the store runs. They are
stored on the connection, never logged, never returned by any endpoint in this
package, and `Connection.describe()` is the only sanctioned way to look at one.
The store's own database is the trust boundary for them, exactly as it is for
everything else a store holds.
"""

from django.db import models

#: The location a connection belongs to, for a merchant with more than one
#: branch — a marketplace account is usually per site. The same bare code the
#: other operational Features carry and `multi-location` names.
DEFAULT_LOCATION = ""


class ProviderKind(models.TextChoices):
    """
    What kind of system is on the other end.

    It decides what a connection is *for* rather than who it is with, and the
    three are genuinely different in their failure modes: a marketplace sends
    orders in and must never lose one, a POS is a second source of truth for the
    same orders, and an accounting system only ever receives and is allowed to be
    a day behind.
    """

    MARKETPLACE = "marketplace", "A delivery or listing marketplace"
    POS = "pos", "A point-of-sale system"
    ACCOUNTING = "accounting", "An accounting or bookkeeping system"


class ConnectionState(models.TextChoices):
    CONNECTED = "connected", "Working"
    EXPIRED = "expired", "The credential needs refreshing"
    REVOKED = "revoked", "The other end withdrew access"
    DISCONNECTED = "disconnected", "Switched off here"


class Connection(models.Model):
    """
    One account on one external system.

    Keyed by a slug the merchant chooses, because a shop with two marketplace
    accounts — one per branch — needs to tell them apart in a support
    conversation, and "the second one" is not a name.

    Disconnecting is a state and never a delete. Every message ever exchanged
    points at this row, and deleting it would take the answer to "what did we
    send them in March" with it.
    """

    slug = models.SlugField(max_length=60, unique=True)
    name = models.CharField(max_length=200)
    kind = models.CharField(max_length=20, choices=ProviderKind)

    #: Which adapter speaks to this system. A name in a closed registry rather
    #: than an import path: an integration is code this Feature ships, and a
    #: configurable import path would be a store executing whatever a
    #: configuration said.
    adapter = models.CharField(max_length=40)

    location = models.CharField(max_length=40, blank=True, default=DEFAULT_LOCATION)

    #: The other end's own identifier for this account, so a support conversation
    #: can start with a number both sides recognise.
    external_account_id = models.CharField(max_length=200, blank=True, default="")

    state = models.CharField(max_length=20, choices=ConnectionState, default=ConnectionState.DISCONNECTED)

    #: The credential. **Never logged, never returned by an endpoint, and never
    #: put in an error message** — `describe()` below is the only sanctioned way
    #: to look at a connection, and it reports presence rather than value.
    #:
    #: Stored here rather than delivered as a KNIGHT secret because there is one
    #: per account and it is refreshed while the store runs, which a static
    #: configuration channel cannot express.
    access_token = models.TextField(blank=True, default="")
    refresh_token = models.TextField(blank=True, default="")
    token_expires_at = models.DateTimeField(null=True, blank=True)
    scopes = models.CharField(max_length=500, blank=True, default="")

    #: What went wrong last, in the other end's words. Kept for support and
    #: shown to an operator; never trusted into a page, because it is somebody
    #: else's string.
    last_error = models.CharField(max_length=500, blank=True, default="")
    last_synced_at = models.DateTimeField(null=True, blank=True)

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        db_table = "knight_marketplaces_connection"
        ordering = ("slug",)
        indexes = [
            models.Index(fields=["kind", "state"], name="knight_mkt_conn_kind"),
        ]

    @property
    def is_usable(self) -> bool:
        return self.state == ConnectionState.CONNECTED

    def describe(self) -> dict:
        """
        What an operator may see about a connection.

        Credentials by presence and never by value. A token echoed by a
        debugging endpoint is a token in a log aggregator, and this one can place
        orders on a marketplace in the merchant's name.
        """
        return {
            "slug": self.slug,
            "name": self.name,
            "kind": self.kind,
            "adapter": self.adapter,
            "state": self.state,
            "location": self.location,
            "externalAccountId": self.external_account_id,
            "hasAccessToken": bool(self.access_token),
            "hasRefreshToken": bool(self.refresh_token),
            "tokenExpiresAt": self.token_expires_at.isoformat() if self.token_expires_at else None,
            "scopes": [scope for scope in self.scopes.split() if scope],
            "lastError": self.last_error,
            "lastSyncedAt": self.last_synced_at.isoformat() if self.last_synced_at else None,
        }

    def __str__(self) -> str:
        return f"{self.slug} ({self.kind})"


class Direction(models.TextChoices):
    INBOUND = "inbound", "From them to us"
    OUTBOUND = "outbound", "From us to them"


class MessageState(models.TextChoices):
    """
    Where a message is.

    `abandoned` is deliberately not called `failed`. A failed message is one that
    will be tried again; an abandoned one has used its attempts and is waiting
    for a person. A merchant needs to be able to ask "what is stuck" and get the
    second list, not both.
    """

    PENDING = "pending", "Waiting to be handled"
    SENT = "sent", "Delivered to them"
    PROCESSED = "processed", "Taken in from them"
    FAILED = "failed", "Will be tried again"
    ABANDONED = "abandoned", "Out of attempts, waiting for a person"
    DISCARDED = "discarded", "Deliberately not handled"


class Message(models.Model):
    """
    One thing that crossed, or is trying to cross, the boundary.

    The row the whole Feature is built around. It exists **before** anything is
    attempted, which is what makes a crash between "we sent it" and "we recorded
    that we sent it" recoverable: the record is never the second half of that
    pair.

    `external_id` is the **counterpart's** identifier for the event, not ours,
    and that is the point. Our own key would make our retries idempotent and do
    nothing about theirs — and theirs is the one that arrives four times.
    """

    connection = models.ForeignKey(Connection, on_delete=models.CASCADE, related_name="messages")
    direction = models.CharField(max_length=10, choices=Direction)

    #: What this message is about: `order.placed`, `stock.updated`,
    #: `invoice.issued`. Free text within a connection because every provider has
    #: its own vocabulary, and normalising it here would be inventing a canonical
    #: model of every marketplace in the world.
    kind = models.CharField(max_length=80)

    #: The counterpart's event id, for an inbound message. Unique per connection,
    #: which is what makes a redelivery a no-op rather than a duplicate order.
    external_id = models.CharField(max_length=200, blank=True, default="")

    #: What this message is about on our side — `("order", "4471")`. Strings
    #: rather than a foreign key, for the reason every Feature here uses one: a
    #: Feature may not reference a store's tables.
    subject_type = models.CharField(max_length=40, blank=True, default="")
    subject_id = models.CharField(max_length=100, blank=True, default="")

    #: The body, as it was. Kept whole rather than parsed into columns: a
    #: marketplace changes its payload shape without telling anybody, and the
    #: only thing that survives that is what they actually sent.
    payload = models.JSONField(default=dict, blank=True)

    state = models.CharField(max_length=12, choices=MessageState, default=MessageState.PENDING)
    attempts = models.PositiveSmallIntegerField(default=0)

    #: When this becomes eligible again. Read **by time** rather than by "has the
    #: worker run", the same rule every scheduled thing in this catalogue
    #: follows.
    next_attempt_at = models.DateTimeField(null=True, blank=True)

    last_error = models.CharField(max_length=500, blank=True, default="")

    #: What the other end called it once they had it, so a support conversation
    #: can quote a number they recognise.
    remote_reference = models.CharField(max_length=200, blank=True, default="")

    created_at = models.DateTimeField(auto_now_add=True)
    settled_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        db_table = "knight_marketplaces_message"
        ordering = ("-created_at", "-id")
        indexes = [
            # The query the flush worker makes on every run.
            models.Index(fields=["state", "next_attempt_at"], name="knight_mkt_msg_due"),
            models.Index(fields=["connection", "direction", "state"], name="knight_mkt_msg_queue"),
            models.Index(fields=["subject_type", "subject_id"], name="knight_mkt_msg_subject"),
        ]
        constraints = [
            # The constraint that makes a redelivery free. Partial, because an
            # outbound message has no counterpart id to be unique on and a blank
            # string would collide with every other blank string.
            models.UniqueConstraint(
                fields=["connection", "external_id"],
                condition=~models.Q(external_id=""),
                name="knight_mkt_one_message_per_external_id",
            ),
        ]

    @property
    def is_settled(self) -> bool:
        return self.state in {
            MessageState.SENT,
            MessageState.PROCESSED,
            MessageState.ABANDONED,
            MessageState.DISCARDED,
        }

    def __str__(self) -> str:
        return f"{self.direction} {self.kind} ({self.state})"


class LinkKind(models.TextChoices):
    PRODUCT = "product", "A product or variant"
    ORDER = "order", "An order"
    CUSTOMER = "customer", "A customer"


class RemoteLink(models.Model):
    """
    What one of our things is called on the other end.

    The table reconciliation is impossible without. Two shops' worth of
    experience is compressed into the pair of unique constraints below: one
    local thing maps to one remote thing **and** one remote thing maps back to
    one local thing, per connection and per kind. Without the second, two of our
    products both claiming the same marketplace listing is a bug that surfaces as
    a stock level oscillating.
    """

    connection = models.ForeignKey(Connection, on_delete=models.CASCADE, related_name="links")
    kind = models.CharField(max_length=20, choices=LinkKind)

    #: Our identifier: a SKU, an order number, a customer reference.
    local_reference = models.CharField(max_length=200)

    #: Theirs.
    remote_id = models.CharField(max_length=200)

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        db_table = "knight_marketplaces_link"
        ordering = ("connection", "kind", "local_reference")
        constraints = [
            models.UniqueConstraint(
                fields=["connection", "kind", "local_reference"],
                name="knight_mkt_one_remote_per_local",
            ),
            models.UniqueConstraint(
                fields=["connection", "kind", "remote_id"],
                name="knight_mkt_one_local_per_remote",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.local_reference} = {self.remote_id}"


class ReconciliationRun(models.Model):
    """
    One comparison of what we think against what they think.

    A run is recorded even when it finds nothing, because "we checked and it was
    fine" is a different fact from "nobody has checked since Tuesday", and only
    the first one lets a merchant sleep.
    """

    connection = models.ForeignKey(Connection, on_delete=models.CASCADE, related_name="reconciliations")
    kind = models.CharField(max_length=20, choices=LinkKind)

    started_at = models.DateTimeField(auto_now_add=True)
    finished_at = models.DateTimeField(null=True, blank=True)

    checked = models.PositiveIntegerField(default=0)
    differing = models.PositiveIntegerField(default=0)

    #: Set when the run itself could not complete — the other end was down, the
    #: credential had expired. Distinct from finding differences, which is the
    #: run working.
    failure = models.CharField(max_length=500, blank=True, default="")

    class Meta:
        db_table = "knight_marketplaces_reconciliation"
        ordering = ("-started_at",)
        indexes = [
            models.Index(fields=["connection", "-started_at"], name="knight_mkt_recon_recent"),
        ]

    def __str__(self) -> str:
        return f"{self.connection_id} {self.kind} ({self.differing} differing)"


class DifferenceKind(models.TextChoices):
    MISSING_THERE = "missing-there", "We have it and they do not"
    MISSING_HERE = "missing-here", "They have it and we do not"
    DIFFERENT = "different", "Both have it and they disagree"


class Discrepancy(models.Model):
    """
    One thing the two sides disagree about.

    **Recorded, never fixed.** A difference between a store and a marketplace
    usually means one of them is right, and which one is a judgement: a price
    that differs may be a marketplace's commission model rather than an error,
    and a missing order may be one they cancelled. A Feature that quietly
    corrected either side would be a Feature that occasionally overwrote the
    truth with the wrong number, silently, on a timer.

    Resolving one is a decision somebody records, which is what `resolved_at` and
    `resolution` are.
    """

    run = models.ForeignKey(ReconciliationRun, on_delete=models.CASCADE, related_name="differences")
    kind = models.CharField(max_length=20, choices=DifferenceKind)

    local_reference = models.CharField(max_length=200, blank=True, default="")
    remote_id = models.CharField(max_length=200, blank=True, default="")

    #: What differs and how, in words a merchant can act on rather than a diff of
    #: two dictionaries.
    detail = models.CharField(max_length=500, blank=True, default="")

    resolved_at = models.DateTimeField(null=True, blank=True)
    resolution = models.CharField(max_length=500, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_marketplaces_difference"
        ordering = ("-created_at",)
        indexes = [
            models.Index(fields=["run", "kind"], name="knight_mkt_diff_run"),
            models.Index(fields=["resolved_at"], name="knight_mkt_diff_open"),
        ]

    def __str__(self) -> str:
        return f"{self.kind}: {self.local_reference or self.remote_id}"
