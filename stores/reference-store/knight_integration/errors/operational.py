"""
Telling KNIGHT about a failure that is nobody's exception.

The error reporter carries unhandled exceptions, which is most of what a store
has to say about itself and not all of it. Three of this architecture's failures
never raise anything a middleware could catch:

- a webhook delivery that used every attempt and was **dead-lettered** — the
  record that a Feature a merchant pays for did not hear something;
- a Feature's service that **did not answer** a proxied request, which the store
  turns into a 502 and carries on;
- a Feature whose shared secret has not arrived, so nothing can be signed.

Each of those is handled correctly and locally: the queue keeps the dead letter,
the proxy returns a 502, the shopper sees a page. And each is invisible to
anybody who is not reading this store's log — which is the failure this reports
(`docs/observability.md`).

They travel down the same path as an exception, batched by the same bounded
queue, because a second reporting channel would be a second thing to be down.
What they carry instead of a stack trace is the fact itself: which Feature,
which event or route, and how many attempts it took to give up.
"""

from __future__ import annotations

import logging
from typing import Any

logger = logging.getLogger(__name__)

#: A delivery that used every attempt. Critical rather than a warning: an
#: at-least-once event that was never delivered is a promise this store made and
#: did not keep.
DEAD_LETTERED = "knight.delivery.dead_lettered"

#: A Feature's service that could not be reached, or answered with a failure,
#: for a request a shopper was waiting on.
SERVICE_UNREACHABLE = "knight.service.unreachable"

#: A Feature installed and serving, with no shared secret to sign with. The
#: store refuses rather than sending an unsigned request, and this is how
#: somebody finds out the configuration never arrived.
SERVICE_UNCONFIGURED = "knight.service.unconfigured"


def report(kind: str, message: str, *, feature: str = "", context: dict[str, Any] | None = None) -> bool:
    """
    Reports one operational failure, and never raises.

    Returns whether it was queued, which is what a test asserts on. Reporting is
    off in a store that is not registered with KNIGHT, and that is not an error:
    a store runs perfectly well without a control plane, and the whole point of
    this module is to be the thing that does not take the shop down.
    """
    try:
        from django.utils import timezone

        from ..conf import get_settings
        from .queue import reporter

        config = get_settings()

        if not (config.error_reporting and config.is_registered):
            return False

        reporter().enqueue(
            {
                "occurredAt": timezone.now().isoformat().replace("+00:00", "Z"),
                # The kind goes where an exception's type goes, because that is
                # what KNIGHT groups on: every dead letter across every store
                # lands in one group, which is what makes "this is happening
                # again" a screen rather than a search.
                "exceptionType": kind,
                "message": message[:2000],
                # No endpoint and no status: this did not happen on a request
                # anybody made. Sending a plausible-looking one would put a
                # route on an errors screen that never failed.
                "endpoint": None,
                "httpMethod": None,
                "statusCode": None,
                "stackTrace": "",
                "context": {"feature": feature, **(context or {})},
            }
        )

        return True
    except Exception:  # noqa: BLE001 - see the docstring
        logger.exception("Could not report '%s' to KNIGHT.", kind)

        return False
