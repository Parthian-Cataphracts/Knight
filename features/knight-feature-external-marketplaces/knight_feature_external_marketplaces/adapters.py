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


def _adapter(name: str):
    return ADAPTERS.get(name)


def known() -> list[str]:
    return sorted(ADAPTERS)
