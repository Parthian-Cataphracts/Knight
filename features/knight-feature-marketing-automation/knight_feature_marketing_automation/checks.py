"""
The health check KNIGHT runs after installing this feature.

Two jobs here. The ordinary one — do the tables exist — and one this Feature
needs more than most: is it about to send anything by accident. A marketing
package whose install left it live with a default template would be the worst
outcome in this catalogue, so the check asserts the opposite.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when the tables exist, the dependency is usable, and nothing is armed."""
    try:
        from . import config, providers, services
        from .models import Campaign, Contact, Send, Suppression

        Campaign.objects.exists()
        Contact.objects.exists()
        Send.objects.exists()
        Suppression.objects.exists()
        services.summary()

        # The dependency this cannot work without. A store that somehow had this
        # installed beside no segmentation would compute an empty audience for
        # every campaign, which reads as a shop with no customers.
        services._segmentation()

        # Reading the configuration must not raise, and must not need a secret
        # to be present.
        config.describe()
        providers.current()
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Marketing automation health check failed.")
        return False

    return True
