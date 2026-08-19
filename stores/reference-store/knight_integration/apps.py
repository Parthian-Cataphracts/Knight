"""App configuration for the integration layer."""

from __future__ import annotations

import logging

from django.apps import AppConfig

from .conf import get_settings

logger = logging.getLogger(__name__)


class KnightIntegrationConfig(AppConfig):
    name = "knight_integration"
    verbose_name = "KNIGHT integration"

    def ready(self) -> None:
        """
        Says out loud what the store is configured to do, once, at startup.

        Deliberately no network calls: a store must start whether or not KNIGHT
        is reachable, and a control plane that is down must never stop a
        storefront from serving shoppers. The handshake happens on first use.
        """
        config = get_settings()

        if not config.is_registered:
            logger.warning(
                "This store has no KNIGHT credentials. It will serve shoppers normally and report nothing. "
                "Run `manage.py knight_register` once KNIGHT_CLIENT_ID and KNIGHT_CLIENT_SECRET are set."
            )
            return

        logger.info(
            "KNIGHT integration configured for %s (%s), version %s, error reporting %s, log shipping %s.",
            config.base_url,
            config.environment,
            config.store_version,
            "on" if config.error_reporting else "off",
            "on" if config.log_shipping else "off",
        )
