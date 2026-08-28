"""
What a store may ask this Feature to do.

The published surface. Callers pass connection slugs, kinds and plain
dictionaries and get plain dataclasses back; nothing here returns a model, and
nothing here reads one of the store's.

Four things carry the weight:

- **`receive()` writes before it does anything**, and the counterpart's event id
  is unique per connection. The fourth copy of a webhook is a database refusal,
  which is why a redelivery costs nothing and creates nothing.
- **`queue()` never sends.** It writes a row. Sending happens in `flush()`, on a
  schedule, so a partner being down cannot make a shopper's checkout hang.
- **A failure is retried with widening gaps and then stops.** A queue with no
  ceiling hammers a partner who is already having a bad day, and the abandoned
  state is what a person looks at afterwards.
- **`reconcile()` compares and records. It never fixes.** Which side is right is
  a judgement, and a Feature that quietly corrected either one would occasionally
  overwrite the truth with the wrong number, on a timer, in silence.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta

from django.db import IntegrityError, transaction
from django.db.models import Count, Q
from django.utils import timezone

from . import adapters, config
from .models import (
    Connection,
    ConnectionState,
    DifferenceKind,
    Direction,
    Discrepancy,
    LinkKind,
    Message,
    MessageState,
    ProviderKind,
    ReconciliationRun,
    RemoteLink,
)


class MarketplaceError(RuntimeError):
    """Something a caller asked for that this Feature will not do."""


class UnknownConnection(MarketplaceError):
    """No connection has that slug."""


class DuplicateMessage(MarketplaceError):
    """
    That counterpart event has already been taken in.

    Its own class because it is the *good* outcome of a redelivery, not a fault.
    A caller that treats it as an error will alert every time a partner's retry
    logic fires, which is a thing partners' retry logic does.
    """


@dataclass(frozen=True)
class Queued:
    """A message, as anything outside this Feature sees it."""

    id: int
    connection: str
    direction: str
    kind: str
    state: str
    attempts: int
    external_id: str
    remote_reference: str
    subject: tuple[str, str]
    last_error: str


@dataclass(frozen=True)
class FlushResult:
    """What one pass over the outbound queue did."""

    sent: int = 0
    failed: int = 0
    abandoned: int = 0

    @property
    def total(self) -> int:
        return self.sent + self.failed + self.abandoned


# --- Connections ------------------------------------------------------------


def connect(
    slug: str,
    *,
    name: str = "",
    kind: str = ProviderKind.MARKETPLACE,
    adapter: str = adapters.LOOPBACK,
    access_token: str = "",
    refresh_token: str = "",
    expires_at: datetime | None = None,
    scopes: str = "",
    external_account_id: str = "",
    location: str = "",
) -> dict:
    """
    Creates or updates a connection and marks it usable.

    The adapter is checked against the registry here rather than at send time,
    because a connection naming an adapter this Feature does not ship is a
    mistake somebody can fix now, and a queue full of messages for it is a
    mistake somebody finds on Monday.

    Returns the connection **described** rather than the row: the caller has no
    business holding a token, and returning one would put it in whatever logged
    the response.
    """
    if adapter not in adapters.known():
        raise MarketplaceError(
            f"'{adapter}' is not an adapter this Feature ships. Known: {', '.join(adapters.known())}."
        )

    if kind not in ProviderKind.values:
        raise MarketplaceError(f"'{kind}' is not a kind of system this Feature knows.")

    connection, _ = Connection.objects.update_or_create(
        slug=_slug(slug),
        defaults={
            "name": name or slug,
            "kind": kind,
            "adapter": adapter,
            "access_token": access_token,
            "refresh_token": refresh_token,
            "token_expires_at": expires_at,
            "scopes": scopes.strip()[:500],
            "external_account_id": external_account_id.strip()[:200],
            "location": location,
            "state": ConnectionState.CONNECTED,
            "last_error": "",
        },
    )

    return connection.describe()


def disconnect(slug: str, *, reason: str = "") -> dict:
    """
    Switches a connection off without deleting it.

    Every message ever exchanged points at the row, and deleting it would take
    the answer to "what did we send them in March" with it. The credential is
    cleared, though: a disconnected account should not leave a usable token
    sitting in the database.
    """
    connection = _connection(slug)

    connection.state = ConnectionState.DISCONNECTED
    connection.access_token = ""
    connection.refresh_token = ""
    connection.last_error = reason.strip()[:500]
    connection.save(update_fields=["state", "access_token", "refresh_token", "last_error", "updated_at"])

    return connection.describe()


def connections(*, kind: str = "", usable_only: bool = False) -> list[dict]:
    found = Connection.objects.all()

    if kind:
        found = found.filter(kind=kind)

    if usable_only:
        found = found.filter(state=ConnectionState.CONNECTED)

    return [connection.describe() for connection in found]


def describe(slug: str) -> dict:
    return _connection(slug).describe()


# --- Taking things in -------------------------------------------------------


@transaction.atomic
def receive(
    slug: str,
    *,
    kind: str,
    external_id: str,
    payload: dict | None = None,
    subject_type: str = "",
    subject_id: str = "",
) -> Queued:
    """
    Takes in one thing a partner sent, exactly once.

    The row is written **first**, before any of the store's own handling, which
    is what makes a crash between "we processed it" and "we recorded it" survive:
    the record is never the second half of that pair.

    `external_id` is the partner's own id for the event. A redelivery raises
    `DuplicateMessage` rather than creating a second row, and the caller's right
    response to that is to answer 200 and move on — which is exactly what makes
    the partner stop retrying.
    """
    connection = _connection(slug)

    if not external_id.strip():
        # Refused rather than accepted with a blank key, because a blank key is
        # not unique and the whole guarantee rests on this field.
        raise MarketplaceError(
            "An inbound message must carry the partner's own event id, or it cannot be de-duplicated."
        )

    try:
        message = Message.objects.create(
            connection=connection,
            direction=Direction.INBOUND,
            kind=kind[:80],
            external_id=external_id.strip()[:200],
            subject_type=subject_type[:40],
            subject_id=str(subject_id)[:100],
            payload=payload or {},
            state=MessageState.PENDING,
        )
    except IntegrityError as exc:
        raise DuplicateMessage(
            f"{connection.slug} has already taken in event '{external_id}'."
        ) from exc

    return _queued(message)


@transaction.atomic
def mark_processed(message_id: int, *, subject_type: str = "", subject_id: str = "", now=None) -> Queued:
    """
    Records that the store has done whatever an inbound message asked for.

    Separate from `receive` deliberately. Taking a message in is this Feature's
    job and acting on it is the store's, and collapsing the two would mean a
    store's own failure to create an order left the partner's event looking
    un-received — so the partner would send it again, and the store would try
    again, for ever.
    """
    now = now or timezone.now()
    message = _message(message_id)

    message.state = MessageState.PROCESSED
    message.settled_at = now

    if subject_type:
        message.subject_type = subject_type[:40]
        message.subject_id = str(subject_id)[:100]

    message.save(update_fields=["state", "settled_at", "subject_type", "subject_id"])

    return _queued(message)


def pending_inbound(*, slug: str = "", limit: int = 200) -> list[Queued]:
    """What has arrived and not yet been acted on by the store."""
    found = Message.objects.filter(direction=Direction.INBOUND, state=MessageState.PENDING)

    if slug:
        found = found.filter(connection__slug=_slug(slug))

    return [_queued(message) for message in found.select_related("connection").order_by("created_at")[:limit]]


# --- Sending things out -----------------------------------------------------


def queue(
    slug: str,
    *,
    kind: str,
    payload: dict | None = None,
    subject_type: str = "",
    subject_id: str = "",
    now=None,
) -> Queued:
    """
    Writes an outbound message. **Does not send it.**

    That separation is the reason a partner being slow cannot make a shopper's
    checkout hang: the store's own code writes a row and carries on, and delivery
    happens on the queue's schedule.
    """
    now = now or timezone.now()
    connection = _connection(slug)

    message = Message.objects.create(
        connection=connection,
        direction=Direction.OUTBOUND,
        kind=kind[:80],
        subject_type=subject_type[:40],
        subject_id=str(subject_id)[:100],
        payload=payload or {},
        state=MessageState.PENDING,
        next_attempt_at=now,
    )

    return _queued(message)


def flush(*, slug: str = "", limit: int = 200, now=None) -> FlushResult:
    """
    Sends what is due. The entrypoint of the hourly worker.

    Each message is handled in its own transaction, deliberately: one partner
    timing out must not roll back the fifty messages that went before it.

    A message whose attempts are used up becomes `abandoned` rather than being
    retried for ever. That is a decision this Feature makes on the merchant's
    behalf and it is the right one — a queue with no ceiling keeps hammering a
    partner who is already having a bad day, and every one of those attempts is
    a rate limit closer to the whole account being blocked.
    """
    now = now or timezone.now()
    result = FlushResult()

    due = Message.objects.filter(
        direction=Direction.OUTBOUND,
        state__in=[MessageState.PENDING, MessageState.FAILED],
    ).filter(Q(next_attempt_at__isnull=True) | Q(next_attempt_at__lte=now))

    if slug:
        due = due.filter(connection__slug=_slug(slug))

    for message_id in list(due.order_by("next_attempt_at", "id").values_list("pk", flat=True)[:limit]):
        with transaction.atomic():
            message = Message.objects.select_for_update().select_related("connection").get(pk=message_id)

            if message.is_settled:
                continue

            outcome = _attempt(message, now)

        result = FlushResult(
            sent=result.sent + (1 if outcome == MessageState.SENT else 0),
            failed=result.failed + (1 if outcome == MessageState.FAILED else 0),
            abandoned=result.abandoned + (1 if outcome == MessageState.ABANDONED else 0),
        )

    return result


def _attempt(message: Message, now) -> str:
    """One delivery attempt, and everything that follows from it."""
    connection = message.connection
    delivery = adapters.deliver(connection, message)

    message.attempts += 1

    if delivery.delivered:
        message.state = MessageState.SENT
        message.settled_at = now
        message.remote_reference = delivery.reference[:200]
        message.next_attempt_at = None
        message.last_error = ""
        message.save(
            update_fields=["state", "attempts", "settled_at", "remote_reference", "next_attempt_at", "last_error"]
        )

        connection.last_synced_at = now
        connection.save(update_fields=["last_synced_at", "updated_at"])

        return MessageState.SENT

    message.last_error = delivery.detail[:500]

    if delivery.credential_failed:
        # Retrying a revoked token a hundred times is how a store gets its whole
        # account rate-limited. The connection is marked instead, which is what
        # an operator needs to see.
        connection.state = ConnectionState.EXPIRED
        connection.last_error = delivery.detail[:500]
        connection.save(update_fields=["state", "last_error", "updated_at"])

    if message.attempts >= config.max_attempts():
        message.state = MessageState.ABANDONED
        message.next_attempt_at = None
        message.settled_at = now
        message.save(update_fields=["state", "attempts", "next_attempt_at", "settled_at", "last_error"])

        return MessageState.ABANDONED

    message.state = MessageState.FAILED
    message.next_attempt_at = now + timedelta(seconds=config.backoff_seconds(message.attempts))
    message.save(update_fields=["state", "attempts", "next_attempt_at", "last_error"])

    return MessageState.FAILED


@transaction.atomic
def replay(message_id: int, *, now=None) -> Queued:
    """
    Puts an abandoned message back in the queue.

    By hand, by a person, after they have fixed whatever it was. The attempt
    count is reset because the previous attempts were against a broken world and
    counting them would abandon it again after one try.
    """
    now = now or timezone.now()
    message = _message(message_id)

    if message.state != MessageState.ABANDONED:
        raise MarketplaceError(f"Message {message_id} is {message.state}, not abandoned.")

    message.state = MessageState.PENDING
    message.attempts = 0
    message.next_attempt_at = now
    message.settled_at = None
    message.save(update_fields=["state", "attempts", "next_attempt_at", "settled_at"])

    return _queued(message)


def abandoned(*, limit: int = 200) -> list[Queued]:
    """What is stuck and waiting for a person."""
    return [
        _queued(message)
        for message in Message.objects.filter(state=MessageState.ABANDONED)
        .select_related("connection")
        .order_by("-created_at")[:limit]
    ]


# --- Mapping ----------------------------------------------------------------


def link(slug: str, kind: str, local_reference: str, remote_id: str) -> RemoteLink:
    """
    Records what one of our things is called on the other end.

    Both directions are unique, which is what stops two of our products claiming
    the same marketplace listing — a bug that surfaces as a stock level
    oscillating and takes a day to find.
    """
    connection = _connection(slug)

    if kind not in LinkKind.values:
        raise MarketplaceError(f"'{kind}' is not a kind of link this Feature knows.")

    existing = RemoteLink.objects.filter(
        connection=connection, kind=kind, local_reference=local_reference
    ).first()

    if existing is not None:
        if existing.remote_id != remote_id:
            existing.remote_id = remote_id
            existing.save(update_fields=["remote_id", "updated_at"])

        return existing

    try:
        return RemoteLink.objects.create(
            connection=connection,
            kind=kind,
            local_reference=local_reference[:200],
            remote_id=remote_id[:200],
        )
    except IntegrityError as exc:
        raise MarketplaceError(
            f"'{remote_id}' is already linked to something else on {connection.slug}."
        ) from exc


def linked(slug: str, kind: str) -> dict[str, str]:
    """`{local_reference: remote_id}` for one connection and kind."""
    return {
        item.local_reference: item.remote_id
        for item in RemoteLink.objects.filter(connection__slug=_slug(slug), kind=kind)
    }


# --- Reconciliation ---------------------------------------------------------


def reconcile(slug: str, kind: str = LinkKind.PRODUCT, *, now=None) -> ReconciliationRun:
    """
    Compares what we think against what they think, and writes down the
    differences.

    **It changes nothing on either side.** Which one is right is a judgement — a
    price that differs may be a marketplace's commission rather than an error,
    and a missing order may be one they cancelled — and a Feature that corrected
    either side automatically would occasionally overwrite the truth with the
    wrong number, silently, on a timer.

    A run that could not ask is recorded as a failure rather than as "nothing
    differs". Reading a failed call as an empty remote would report every product
    this store sells as missing from the marketplace, which is an alarming way to
    say the network was down.
    """
    now = now or timezone.now()
    connection = _connection(slug)
    run = ReconciliationRun.objects.create(connection=connection, kind=kind)

    remote = adapters.snapshot(connection, kind)

    if not remote.available:
        run.failure = remote.detail[:500]
        run.finished_at = timezone.now()
        run.save(update_fields=["failure", "finished_at"])

        return run

    local = linked(connection.slug, kind)
    differing = 0

    for local_reference, remote_id in local.items():
        if remote_id not in remote.items:
            Discrepancy.objects.create(
                run=run,
                kind=DifferenceKind.MISSING_THERE,
                local_reference=local_reference,
                remote_id=remote_id,
                detail="This store has it linked and the other end does not have it.",
            )
            differing += 1

    known_remote = set(local.values())

    for remote_id in remote.items:
        if remote_id not in known_remote:
            Discrepancy.objects.create(
                run=run,
                kind=DifferenceKind.MISSING_HERE,
                remote_id=remote_id,
                detail="The other end has it and this store has nothing linked to it.",
            )
            differing += 1

    run.checked = len(local) + len(remote.items)
    run.differing = differing
    run.finished_at = timezone.now()
    run.save(update_fields=["checked", "differing", "finished_at"])

    return run


def open_differences(*, limit: int = 200) -> list[Discrepancy]:
    """Everything the two sides disagree about that nobody has resolved."""
    return list(
        Discrepancy.objects.filter(resolved_at__isnull=True)
        .select_related("run__connection")
        .order_by("-created_at")[:limit]
    )


def resolve(difference_id: int, *, resolution: str, now=None) -> Discrepancy:
    """Records that a person decided what to do about a difference."""
    now = now or timezone.now()
    difference = Discrepancy.objects.filter(pk=difference_id).first()

    if difference is None:
        raise MarketplaceError(f"No difference has the id {difference_id}.")

    difference.resolved_at = now
    difference.resolution = resolution.strip()[:500]
    difference.save(update_fields=["resolved_at", "resolution"])

    return difference


# --- Credentials ------------------------------------------------------------

#: How long before a token expires this Feature tries to renew it.
#:
#: An hour, against a sweep that runs hourly. The window has to be at least as
#: long as the gap between sweeps or a token can expire between two of them,
#: which is the failure this whole thing exists to prevent — and it is a failure
#: nobody sees until a partner starts refusing a store's orders.
RENEW_WITHIN = timedelta(hours=1)


def refresh(slug: str, *, force: bool = False, now=None) -> dict:
    """
    Renews one connection's access token, if it is close enough to expiring.

    A credential nobody rotates is the same problem as a shared secret nobody
    rotates, shaped differently: this Feature has stored a refresh token since it
    was written and used it for nothing, so a store connected in January stops
    working in February and the reason is invisible from inside the shop.

    Three outcomes, and they are deliberately not two:

    - **renewed** — the new token is stored and the connection stays usable;
    - **not yet** — the token has time left, or there is nothing to renew with.
      Not a failure: a connection given a long-lived token and no refresh token
      is a supported arrangement;
    - **the credential is dead** — the other end rejected the refresh token, so
      the connection is marked `expired` and stops being swept. A hundred
      retries against a revoked token is how a store gets rate-limited by a
      partner it can no longer talk to anyway.

    Returns the connection described, plus what happened. Never the token.
    """
    now = now or timezone.now()
    connection = _connection(slug)

    if connection.state not in {ConnectionState.CONNECTED, ConnectionState.EXPIRED}:
        return {**connection.describe(), "renewed": False, "detail": "This connection is switched off."}

    if not force and not _renewable(connection, now):
        return {**connection.describe(), "renewed": False, "detail": "The token is not close to expiring."}

    outcome = adapters.refresh(connection)

    if not outcome.renewed:
        if outcome.credential_failed:
            # Expired rather than disconnected: the account is still connected in
            # every sense a merchant means, and what it needs is somebody to sign
            # in again. Disconnecting would clear the tokens and lose the trail.
            connection.state = ConnectionState.EXPIRED

        connection.last_error = outcome.detail[:500]
        connection.save(update_fields=["state", "last_error", "updated_at"])

        return {**connection.describe(), "renewed": False, "detail": outcome.detail}

    connection.access_token = outcome.access_token

    # An empty refresh token means the provider did not rotate it, never that it
    # has none. Overwriting it here would disconnect the account at the next
    # renewal, which is a fortnight later and nowhere near the change that
    # caused it.
    if outcome.refresh_token:
        connection.refresh_token = outcome.refresh_token

    connection.token_expires_at = outcome.expires_at
    connection.state = ConnectionState.CONNECTED
    connection.last_error = ""
    connection.save(
        update_fields=[
            "access_token",
            "refresh_token",
            "token_expires_at",
            "state",
            "last_error",
            "updated_at",
        ]
    )

    return {**connection.describe(), "renewed": True, "detail": ""}


def refresh_due(*, limit: int = 200, now=None) -> dict[str, int]:
    """
    Renews every credential close enough to expiring, and says what happened.

    Due-ness is time, never "has the sweep run". A store whose cron is broken
    renews late; a store whose renewal depended on the sweep having run would
    renew wrongly.
    """
    now = now or timezone.now()
    counts = {"renewed": 0, "failed": 0, "expired": 0}

    due = (
        Connection.objects.filter(state=ConnectionState.CONNECTED)
        .exclude(refresh_token="")
        .filter(Q(token_expires_at__isnull=False) & Q(token_expires_at__lte=now + RENEW_WITHIN))
        .order_by("token_expires_at")[:limit]
    )

    for connection in list(due):
        outcome = refresh(connection.slug, force=True, now=now)

        if outcome["renewed"]:
            counts["renewed"] += 1
        elif outcome["state"] == ConnectionState.EXPIRED:
            counts["expired"] += 1
        else:
            counts["failed"] += 1

    return counts


def _renewable(connection, now) -> bool:
    """
    Whether this connection is close enough to expiring to be worth renewing.

    A token with no expiry is left alone. The provider did not say when it ends,
    and renewing one every hour on a guess is a request nobody asked for against
    a rate limit somebody set.
    """
    if not connection.refresh_token or connection.token_expires_at is None:
        return False

    return connection.token_expires_at <= now + RENEW_WITHIN


# --- Workers ----------------------------------------------------------------


def run_token_refresh() -> dict[str, int]:
    """
    Entrypoint for the hourly worker the manifest declares.

    Hourly, matched to `RENEW_WITHIN`: a window shorter than the gap between
    sweeps lets a token expire between two of them.
    """
    return refresh_due()


def run_flush() -> dict[str, int]:
    """Entrypoint for the hourly worker the manifest declares."""
    result = flush()

    return {"sent": result.sent, "failed": result.failed, "abandoned": result.abandoned}


def run_reconciliation() -> dict[str, int]:
    """
    Entrypoint for the daily worker the manifest declares.

    Every usable connection, every kind it could have. Daily because a
    reconciliation is a morning report rather than an alarm — and because asking
    a partner for a full snapshot hourly is how a store discovers a rate limit.
    """
    counts = {"runs": 0, "differing": 0, "unavailable": 0}

    for described in connections(usable_only=True):
        for kind in LinkKind.values:
            run = reconcile(described["slug"], kind)
            counts["runs"] += 1
            counts["differing"] += run.differing

            if run.failure:
                counts["unavailable"] += 1

    return counts


# --- Reading ----------------------------------------------------------------


def queue_depth() -> dict[str, int]:
    """
    How much is waiting, by state.

    The number an operator looks at first, and the one worth putting on a
    dashboard: a queue that is growing is a partner that is down, and it shows
    here before it shows anywhere else.
    """
    counts = dict(
        Message.objects.filter(direction=Direction.OUTBOUND)
        .values_list("state")
        .annotate(total=Count("pk"))
    )

    return {state: counts.get(state, 0) for state in MessageState.values}


def already_queued(subject_type: str, subject_ids) -> set[tuple[str, str]]:
    """
    Which `(connection slug, subject id)` pairs already have a message.

    Exists so a store can be idempotent without holding this Feature's models. A
    caller that reached for `Message.objects` to work this out would be a store
    reading a Feature's tables, which is the one thing the delivery model does
    not allow in either direction - and the caller is usually a cron entry, so
    getting this wrong means an accounting system receiving the same invoice
    twice.
    """
    wanted = [str(subject_id) for subject_id in subject_ids]

    if not wanted:
        return set()

    return {
        (slug, subject_id)
        for slug, subject_id in Message.objects.filter(
            subject_type=subject_type[:40], subject_id__in=wanted
        ).values_list("connection__slug", "subject_id")
    }


def messages(*, slug: str = "", direction: str = "", state: str = "", limit: int = 200) -> list[Queued]:
    found = Message.objects.all()

    if slug:
        found = found.filter(connection__slug=_slug(slug))

    if direction:
        found = found.filter(direction=direction)

    if state:
        found = found.filter(state=state)

    return [_queued(message) for message in found.select_related("connection")[:limit]]


# --- Internals --------------------------------------------------------------


def _queued(message: Message) -> Queued:
    return Queued(
        id=message.pk,
        connection=message.connection.slug,
        direction=message.direction,
        kind=message.kind,
        state=message.state,
        attempts=message.attempts,
        external_id=message.external_id,
        remote_reference=message.remote_reference,
        subject=(message.subject_type, message.subject_id),
        last_error=message.last_error,
    )


def _connection(slug: str) -> Connection:
    found = Connection.objects.filter(slug=_slug(slug)).first()

    if found is None:
        raise UnknownConnection(f"No connection has the slug '{slug}'.")

    return found


def _message(message_id: int) -> Message:
    found = Message.objects.select_related("connection").filter(pk=message_id).first()

    if found is None:
        raise MarketplaceError(f"No message has the id {message_id}.")

    return found


def _slug(value: str) -> str:
    return str(value or "").strip().lower()
