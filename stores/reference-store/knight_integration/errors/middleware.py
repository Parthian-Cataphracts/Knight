"""
The middleware that turns an unhandled exception into a report.

It observes and re-raises. Django's own error handling is untouched: the shopper
gets exactly the response they would have got without KNIGHT, and the report is
a side effect that happens on another thread.

Reporting is skipped entirely when the store has no credentials or has error
reporting switched off, so a store can be run — and developed — without a
control plane at all.
"""

from __future__ import annotations

import logging
import traceback
from typing import Any, Callable

from django.http import HttpRequest, HttpResponse

from ..conf import get_settings
from .queue import reporter
from .scrub import describe_request

logger = logging.getLogger(__name__)

MAX_STACK_TRACE_CHARS = 20000


class KnightErrorReportingMiddleware:
    """Reports unhandled exceptions to KNIGHT, then gets out of the way."""

    def __init__(self, get_response: Callable[[HttpRequest], HttpResponse]) -> None:
        self.get_response = get_response
        config = get_settings()
        self._enabled = config.error_reporting and config.is_registered

        if not self._enabled:
            logger.info(
                "KNIGHT error reporting is off: %s",
                "no credentials are configured" if not config.is_registered else "it is disabled by configuration",
            )

    def __call__(self, request: HttpRequest) -> HttpResponse:
        return self.get_response(request)

    def process_exception(self, request: HttpRequest, exception: Exception) -> None:
        """
        Returning None hands the exception straight back to Django. Nothing here
        may change how the store answers — reporting an error must not be able to
        turn a 500 into something else, or a report failure into a second error.
        """
        if not self._enabled:
            return None

        try:
            reporter().enqueue(build_event(request, exception))
        except Exception:  # noqa: BLE001 - never let reporting break the response
            logger.exception("Could not queue an error report for KNIGHT.")

        return None


def build_event(request: HttpRequest | None, exception: BaseException, status_code: int = 500) -> dict[str, Any]:
    """
    Builds the ingestion payload for one exception.

    Kept separate from the middleware so a management command, a Celery task or a
    test can report an exception the same way a request does.
    """
    from django.utils import timezone

    return {
        "occurredAt": timezone.now().isoformat().replace("+00:00", "Z"),
        "exceptionType": type(exception).__name__,
        "message": str(exception)[:2000] or type(exception).__name__,
        "endpoint": getattr(request, "path", None),
        "httpMethod": getattr(request, "method", None),
        "statusCode": status_code,
        "stackTrace": "".join(
            traceback.format_exception(type(exception), exception, exception.__traceback__)
        )[:MAX_STACK_TRACE_CHARS],
        "requestId": _request_id(request),
        "traceId": _trace_id(request),
        "context": describe_request(request) if request is not None else {},
    }


def _request_id(request: HttpRequest | None) -> str | None:
    if request is None:
        return None

    return request.headers.get("X-Request-Id") or request.headers.get("X-Correlation-Id")


def _trace_id(request: HttpRequest | None) -> str | None:
    """
    The trace id out of a W3C traceparent, when one is present. Format:
    version-traceid-spanid-flags; the trace id is the part that ties a store
    error to a KNIGHT request (docs/observability.md).
    """
    if request is None:
        return None

    traceparent = request.headers.get("traceparent")
    if not traceparent:
        return None

    parts = traceparent.split("-")
    return parts[1] if len(parts) >= 2 else None
