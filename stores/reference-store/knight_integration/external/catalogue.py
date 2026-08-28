"""
The events this store publishes and the places a Feature's screens can hang.

Both are closed lists, and both are checked at install rather than at the first
delivery. A Feature subscribing to ``order.plaecd`` would otherwise install
cleanly, pass its health check, and never hear anything — and the person who
notices is the merchant, weeks later, wondering why their subscriptions were
never renewed.

This is the store's half of the contract. KNIGHT validates the *shape* of an
event name at publish, because it cannot know what any particular store
publishes; the store validates the *name* at install, because it is the only
thing that can.
"""

from __future__ import annotations

#: Business events this store emits, as ``domain.thing_happened``.
#:
#: Past tense on purpose: a subscriber is being told something has already
#: happened, not asked whether it may. A Feature that needs to *prevent* an
#: order is not a webhook subscriber and this store does not offer that — a
#: synchronous veto from a third-party service is a checkout that goes down when
#: somebody else's server does.
KNOWN_EVENTS: frozenset[str] = frozenset(
    {
        "order.placed",
        "order.paid",
        "order.cancelled",
        "order.refunded",
        "order.fulfilled",
        "cart.abandoned",
        "customer.registered",
        "customer.updated",
        "product.created",
        "product.updated",
        "product.stock_changed",
        "subscription.renewal_due",
    }
)

#: Where an external Feature's screens may appear.
#:
#: A slot is a promise the store keeps about layout, so the list is short and
#: grows only when somebody has built the place. A Feature naming a slot that
#: does not exist would render nowhere and report success.
UI_SLOTS: frozenset[str] = frozenset(
    {
        "admin.sidebar",
        "admin.order_detail",
        "admin.customer_detail",
        "admin.settings",
        "storefront.account",
    }
)


def is_known_event(name: str) -> bool:
    return name in KNOWN_EVENTS


def is_known_slot(name: str) -> bool:
    return name in UI_SLOTS
