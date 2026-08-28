"""
The things that actually talk to somebody else.

A closed registry rather than a configurable import path, deliberately. An
adapter is code this Feature ships and has tested; a path read from configuration
would be a store executing whatever its configuration said, which on the Feature
with the most third-party surface is the last place to be relaxed about it.

Four adapters, and only one of them moves:

**`loopback`** accepts everything and hands back a reference. It exists so a
store can prove its own wiring — the queue, the retries, the idempotency, the
reconciliation — without an account anywhere, and so this Feature's own tests
exercise the whole path rather than mocking it. It is named `loopback` rather
than anything resembling a vendor precisely so that nobody can mistake a store
running it for a store that is connected to something.

**`marketplace`**, **`pos`** and **`accounting`** are the shapes of real
integrations, and each refuses at the point the vendor call would be made. That
is the same boundary `marketing-automation`, `ai-reports` and `subscriptions`
drew, and for the same reason: wiring a vendor is an integration against a real
account under a real agreement, and a plausible-looking HTTP call to a partner
nobody has an account with is worse than an honest refusal. Everything up to the
refusal is real — the credential is checked, the payload is built, the failure is
recorded and retried on the schedule the queue would use for a genuine outage.

Nothing here raises. A queue that has to decide whether an exception means
"delivered" is a queue that eventually sends something twice, so every adapter
returns a `Delivery` and unexpected exceptions are turned into failures by
`deliver()`.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass, field

logger = logging.getLogger(__name__)

LOOPBACK = "loopback"
MARKETPLACE = "marketplace"
POS = "pos"
ACCOUNTING = "accounting"


@dataclass(frozen=True)
class Delivery:
    """What one attempt to hand something over came back with."""

    delivered: bool
    reference: str = ""
    detail: str = ""

    #: Set when the failure is the credential rather than the message. The queue
    #: reads it to mark the connection expired rather than retrying a token that
    #: will never work again — a hundred retries against a revoked token is how a
    #: partner rate-limits a store.
    credential_failed: bool = False


@dataclass(frozen=True)
class Refreshed:
    """
    What one attempt to renew a credential came back with.

    Separate from `Delivery` because the two failures mean different things. A
    message that did not go can be tried again in an hour; a refresh token the
    other end has rejected will never work again, and retrying it is how a store
    gets rate-limited by a partner it can no longer talk to anyway.
    """

    renewed: bool
    access_token: str = ""

    #: The new refresh token, where the provider rotates them. Empty means keep
    #: the one we have: a provider that returns nothing here has not rotated it,
    #: and overwriting it with an empty string would disconnect the account at
    #: the next renewal.
    refresh_token: str = ""

    #: When the new access token stops working, or None when the provider did not
    #: say. None is treated as "ask again on the next sweep" rather than as
    #: "never expires", because the second guess is the one that fails silently.
    expires_at: object = None

    detail: str = ""

    #: The other end has rejected the refresh token itself. The connection is
    #: marked expired rather than retried.
    credential_failed: bool = False


@dataclass
class Snapshot:
    """What the other end says it has, for one kind of thing."""

    available: bool = False
    detail: str = ""

    #: `{remote_id: {...}}`. Empty and unavailable are different: the first says
    #: they have nothing, the second says we could not ask.
    items: dict = field(default_factory=dict)


def deliver(connection, message) -> Delivery:
    """
    Hands one outbound message to whatever is on the other end.

    Never raises, for the reason in the module docstring. An adapter that threw
    would leave the queue unable to say whether the message arrived, and "we do
    not know" is the answer that eventually becomes "we sent it twice".
    """
    try:
        adapter = _adapter(connection.adapter)

        if adapter is None:
            return Delivery(
                delivered=False,
                detail=f"'{connection.adapter}' is not an adapter this Feature ships.",
            )

        if not connection.is_usable:
            return Delivery(
                delivered=False,
                detail=f"The connection is {connection.state}, so nothing was sent.",
                credential_failed=connection.state in {"expired", "revoked"},
            )

        return adapter(connection, message)
    except Exception as exc:  # noqa: BLE001 - see the docstring
        # Logged without the payload: a marketplace message carries a shopper's
        # address, and a stack trace in a log aggregator is not where it belongs.
        logger.exception("The '%s' adapter raised while delivering.", connection.adapter)

        return Delivery(delivered=False, detail=f"The adapter raised: {type(exc).__name__}.")


def refresh(connection) -> Refreshed:
    """
    Asks the other end for a new access token, using the refresh token.

    Never raises, for the same reason `deliver` does not: the caller has to be
    able to tell "renewed" from "not renewed" from "this credential is dead",
    and an exception collapses the last two into the first kind of unknown that
    ends with a store hammering a partner.
    """
    if not connection.refresh_token:
        # Not a failure of the credential — there is no credential to fail. A
        # connection given a long-lived token and no refresh token is a
        # supported arrangement, and sweeping it must not disconnect it.
        return Refreshed(renewed=False, detail="This connection has no refresh token.")

    adapter = _REFRESHERS.get(connection.adapter)

    if adapter is None:
        return Refreshed(
            renewed=False,
            detail=f"'{connection.adapter}' is not an adapter this Feature ships.",
        )

    try:
        return adapter(connection)
    except Exception as failure:  # noqa: BLE001 - see the docstring
        logger.exception("Refreshing '%s' raised.", connection.slug)

        return Refreshed(renewed=False, detail=str(failure)[:500])


def snapshot(connection, kind: str) -> Snapshot:
    """
    Asks the other end what it has, for reconciliation.

    Unavailable rather than empty when it cannot be asked. A reconciliation that
    read a failed call as "they have nothing" would report every product this
    store sells as missing from the marketplace, which is the most alarming
    possible way to say "the network was down".
    """
    try:
        if connection.adapter == LOOPBACK:
            return _loopback_snapshot(connection, kind)

        if not connection.is_usable:
            return Snapshot(available=False, detail=f"The connection is {connection.state}.")

        return Snapshot(
            available=False,
            detail=(
                f"The '{connection.adapter}' adapter is not wired to a vendor, so there is "
                "nothing to compare against."
            ),
        )
    except Exception as exc:  # noqa: BLE001
        logger.exception("The '%s' adapter raised while snapshotting.", connection.adapter)

        return Snapshot(available=False, detail=f"The adapter raised: {type(exc).__name__}.")


# --- The adapters -----------------------------------------------------------


def _loopback(connection, message) -> Delivery:
    """
    Accepts everything, and can be told to refuse.

    The refusal switch is a key in the payload rather than configuration, so a
    test or a store proving its wiring can produce a failure for one message
    without changing anything global. Nothing leaves the process.
    """
    if message.payload.get("_loopback_fails"):
        return Delivery(
            delivered=False,
            detail=str(message.payload.get("_loopback_fails"))[:500],
            credential_failed=bool(message.payload.get("_loopback_credential_failed")),
        )

    return Delivery(delivered=True, reference=f"loopback:{connection.slug}:{message.pk}")


def _refuses(name: str):
    """The shape of a real integration, refusing where the vendor call would be."""

    def adapter(connection, message) -> Delivery:
        if not connection.access_token:
            return Delivery(
                delivered=False,
                detail=(
                    f"The '{name}' connection has no access token, so nothing was sent. "
                    "Connect the account before queueing messages to it."
                ),
                credential_failed=True,
            )

        # Deliberately not implemented. See the module docstring: which partner,
        # under what agreement, is a commercial decision, and inventing the call
        # would mean shipping code nobody has watched work.
        return Delivery(
            delivered=False,
            detail=(
                f"The '{name}' adapter is connected but not wired to a vendor, so "
                f"'{message.kind}' was not sent."
            ),
        )

    return adapter


def _loopback_refresh(connection) -> Refreshed:
    """
    Mints a new token without leaving the process.

    It exists so a store can prove the whole renewal path — the sweep, the
    window, what happens to the old token, what a rejected refresh does to the
    connection — before any of it is pointed at a partner. The token is
    obviously fake and says so.
    """
    from datetime import timedelta

    from django.utils import timezone

    if connection.refresh_token.startswith("expired:"):
        # The other end saying no. Provoked by the refresh token's own value so
        # a test — or a store proving its wiring — can produce it for one
        # connection without changing anything global, the same way `_loopback`
        # is told to refuse a message.
        return Refreshed(
            renewed=False,
            credential_failed=True,
            detail="The loopback provider rejected this refresh token.",
        )

    now = timezone.now()

    return Refreshed(
        renewed=True,
        access_token=f"loopback-access:{connection.slug}:{int(now.timestamp())}",
        # Rotated, because a provider that rotates refresh tokens is the case
        # that goes wrong: keeping the old one there would work until it did not.
        refresh_token=f"loopback-refresh:{connection.slug}:{int(now.timestamp())}",
        expires_at=now + timedelta(hours=1),
    )


def _refuses_refresh(name: str):
    """The shape of a real renewal, refusing where the vendor call would be."""

    def adapter(connection) -> Refreshed:
        return Refreshed(
            renewed=False,
            detail=(
                f"The '{name}' adapter is not wired to a vendor, so this token was "
                "not renewed. Nothing was sent to anybody."
            ),
        )

    return adapter


def _loopback_snapshot(connection, kind: str) -> Snapshot:
    """
    What loopback claims to hold: exactly what this store has linked to it.

    Which means a loopback reconciliation finds nothing differing unless
    something has been linked and then changed — and that is the useful
    behaviour, because it is the case a store proving its wiring wants to see
    working before it points the same machinery at a real partner.
    """
    from .models import RemoteLink

    links = RemoteLink.objects.filter(connection=connection, kind=kind)

    return Snapshot(
        available=True,
        items={link.remote_id: {"localReference": link.local_reference} for link in links},
    )


ADAPTERS = {
    LOOPBACK: _loopback,
    MARKETPLACE: _refuses(MARKETPLACE),
    POS: _refuses(POS),
    ACCOUNTING: _refuses(ACCOUNTING),
}


#: Who can renew a credential. Deliberately a second registry rather than a
#: method on the first: an adapter that can deliver messages does not
#: necessarily have an OAuth flow behind it, and a missing entry should read as
#: "this one does not renew" rather than crash a sweep.
_REFRESHERS = {
    LOOPBACK: _loopback_refresh,
    MARKETPLACE: _refuses_refresh(MARKETPLACE),
    POS: _refuses_refresh(POS),
    ACCOUNTING: _refuses_refresh(ACCOUNTING),
}


def _adapter(name: str):
    return ADAPTERS.get(name)


def known() -> list[str]:
    return sorted(ADAPTERS)
