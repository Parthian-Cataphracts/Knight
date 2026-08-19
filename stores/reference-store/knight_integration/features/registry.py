"""
Which feature packages this store has installed.

In phase 3.5 this reads a local installation registry written only by the
installer, and the answer is a fact about what is on disk. Today no feature
packages exist yet, so the honest answer is "the capabilities built into the
store itself" — declared here, in one place, rather than assumed by scattered
code.

The distinction is worth keeping even while it is trivial. Entitlement and
installation are separate facts, and code that asks the right question now will
keep working when the answer stops being a constant
(docs/README.md rule 10).
"""

from __future__ import annotations

#: Capabilities the base store implements itself. Not feature packages — those
#: arrive with the registry — but the same vocabulary, so callers do not have to
#: care which is which.
BUILT_IN_FEATURES = ("storefront", "order-management")


def installed_features() -> tuple[str, ...]:
    """Feature slugs this store can serve, reported to KNIGHT in every heartbeat."""
    return BUILT_IN_FEATURES


def is_installed(slug: str) -> bool:
    return slug in installed_features()
