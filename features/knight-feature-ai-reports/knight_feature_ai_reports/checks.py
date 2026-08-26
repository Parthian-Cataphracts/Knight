"""
The health check KNIGHT runs after installing this feature.

Three jobs. The ordinary one, the dependency, and one specific to a Feature that
can spend money: prove the budget exists and that the default configuration is
the one that spends nothing. An AI Feature whose install left it pointed at a
paid provider with no cap would be a bill nobody agreed to.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when the tables exist, analytics is usable, and spending is bounded."""
    try:
        from . import config, providers, services
        from .models import Budget, Finding, Report

        Report.objects.exists()
        Finding.objects.exists()

        # The dependency this Feature reads. Without it every report would be
        # empty, which looks like a quiet week rather than a broken install.
        services._analytics()

        # A budget row must exist before anything can be refused for exceeding
        # it, and the caps must be positive.
        record = Budget.current()

        if record.monthly_token_cap <= 0 or record.monthly_cost_cap < 0:
            logger.error("The narration budget is not a usable limit.")
            return False

        config.describe()
        providers.current()
        services.usage()
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("AI reports health check failed.")
        return False

    return True
