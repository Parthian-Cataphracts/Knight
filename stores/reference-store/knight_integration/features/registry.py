"""
Which feature packages this store has installed.

This reads the local installation registry written only by the installer, so the
answer is a fact about what is on disk rather than what anyone expected to be
there. That distinction is the whole mechanism by which drift becomes visible:
KNIGHT compares what it believes it installed against what the store reports,
and can only do that because the store reports the truth about itself
(docs/feature-delivery.md §14).

The base store's own capabilities are listed separately below. They are not
feature packages — nothing delivered them and nothing can remove them — but they
share the vocabulary so callers do not have to care which is which.

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
    """
    Feature slugs this store can serve, reported to KNIGHT in every heartbeat.

    Only *enabled* features are listed. A feature whose entitlement lapsed is
    still on disk with its data intact, but it is not something this store can
    serve, and reporting it as though it were would tell KNIGHT the opposite of
    what disabling was supposed to achieve.

    A registry that cannot be read falls back to the built-in capabilities. A
    heartbeat that fails to send because a JSON file was unreadable would make a
    healthy store look offline.
    """
    try:
        from ..installer.state import get_registry

        delivered = tuple(feature.slug for feature in get_registry().enabled_features())
    except Exception:  # noqa: BLE001 - a heartbeat must still go out
        import logging

        logging.getLogger(__name__).exception(
            "The feature registry could not be read; reporting built-in capabilities only."
        )
        delivered = ()

    return BUILT_IN_FEATURES + delivered


def is_installed(slug: str) -> bool:
    return slug in installed_features()
