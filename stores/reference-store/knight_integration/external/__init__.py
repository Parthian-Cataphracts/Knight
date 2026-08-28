"""
Features that are services rather than packages.

The store runs none of their code. What it holds for them is three things, and
only three: a list of its own events to forward, a set of route prefixes to
proxy, and a set of places their screens hang
(``docs/adr/0033-api-driven-features.md``).

That is the whole of the integration surface, and its smallness is the point.
An in-process Feature arrives as code with the store's database handle, its
INSTALLED_APPS entry and its migrations; an external one arrives as a JSON
document naming events, prefixes and URLs. The second cannot corrupt the store's
schema, cannot import the store's models, and cannot survive a bad deploy of its
own — because it was never inside the deploy.
"""

from .catalogue import KNOWN_EVENTS, UI_SLOTS, is_known_event, is_known_slot
from .contract import ExternalContract, contract_of, external_features
from .bus import publish, subscribers_for

__all__ = [
    "KNOWN_EVENTS",
    "UI_SLOTS",
    "ExternalContract",
    "contract_of",
    "external_features",
    "is_known_event",
    "is_known_slot",
    "publish",
    "subscribers_for",
]
