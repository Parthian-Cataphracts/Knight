"""
Reporting lifecycle events.

Unlike error reporting, this is synchronous: the caller is a deployment script
or a management command that has time to wait, and knowing whether KNIGHT
recorded the deployment is the point of sending it. A failure is raised rather
than swallowed, so a deploy pipeline can decide for itself whether to care.
"""

from __future__ import annotations

import logging
import uuid
from typing import Any

from django.utils import timezone

from ..conf import get_settings

logger = logging.getLogger(__name__)

DEPLOYMENT_COMPLETED = "deployment.completed"
DEPLOYMENT_FAILED = "deployment.failed"


def report_event(
    event_type: str,
    summary: str,
    severity: str = "Info",
    payload: dict[str, Any] | None = None,
    trace_id: str | None = None,
) -> dict[str, Any]:
    """Sends one lifecycle event and returns KNIGHT's receipt."""
    from ..client import KnightClient

    body = {
        "occurredAt": timezone.now().isoformat().replace("+00:00", "Z"),
        "type": event_type,
        "severity": severity,
        "summary": summary,
        "traceId": trace_id,
        "payload": payload or {},
    }

    return KnightClient().send_events([body], idempotency_key=uuid.uuid4().hex)


def report_deployment(
    version: str | None = None,
    previous_version: str | None = None,
    succeeded: bool = True,
    notes: str | None = None,
) -> dict[str, Any]:
    """
    Tells KNIGHT that this store deployed.

    Worth sending even though KNIGHT notices a version change on its own: a
    report arrives immediately and says whether the deployment worked, where a
    detected change only ever says that something is now running.
    """
    config = get_settings()
    deployed = version or config.store_version

    return report_event(
        DEPLOYMENT_COMPLETED if succeeded else DEPLOYMENT_FAILED,
        notes or f"Deployed {deployed}.",
        severity="Info" if succeeded else "Error",
        payload={"version": deployed, "previousVersion": previous_version},
    )
