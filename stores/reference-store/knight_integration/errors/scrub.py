"""
Removing things that must never leave the store.

Error reports are useful because they carry context, and dangerous for the same
reason. The rule here is a denylist by key name applied recursively, plus a hard
refusal to send request bodies at all: a body is where card numbers and
passwords actually live, and no amount of key matching makes shipping one safe
(docs/store-integration.md §4).

Replaced values become "***" rather than disappearing, so a report still shows
that a field was present.
"""

from __future__ import annotations

from typing import Any

REDACTED = "***"

#: Matched as substrings, case-insensitively, against key names.
SCRUB_KEYS = (
    "password",
    "passwd",
    "secret",
    "token",
    "authorization",
    "auth",
    "cookie",
    "session",
    "csrf",
    "api_key",
    "apikey",
    "credit",
    "card",
    "cvv",
    "iban",
    "ssn",
    "national_id",
    "private",
    "signature",
)

#: Headers worth reporting. An allowlist, because the interesting header is
#: never the one somebody remembered to add to a denylist.
SAFE_HEADERS = ("user-agent", "referer", "accept-language", "content-type", "x-request-id", "traceparent")

_MAX_DEPTH = 6
_MAX_VALUE_LENGTH = 500


def scrub(value: Any, depth: int = 0) -> Any:
    """Returns a copy of ``value`` with sensitive entries replaced."""
    if depth > _MAX_DEPTH:
        return REDACTED

    if isinstance(value, dict):
        return {
            key: REDACTED if is_sensitive(str(key)) else scrub(inner, depth + 1)
            for key, inner in value.items()
        }

    if isinstance(value, (list, tuple)):
        return [scrub(item, depth + 1) for item in value[:20]]

    if isinstance(value, str):
        return value[:_MAX_VALUE_LENGTH]

    if isinstance(value, (int, float, bool)) or value is None:
        return value

    return str(value)[:_MAX_VALUE_LENGTH]


def is_sensitive(key: str) -> bool:
    lowered = key.lower()
    return any(fragment in lowered for fragment in SCRUB_KEYS)


def describe_request(request: Any) -> dict[str, Any]:
    """
    The context worth attaching to an error, and nothing else.

    Query strings are included by key only — a query string is where a password
    reset token ends up — and the body is never included at all.
    """
    context: dict[str, Any] = {}

    headers = getattr(request, "headers", None)
    if headers is not None:
        context["headers"] = {
            name: headers[name][:_MAX_VALUE_LENGTH]
            for name in SAFE_HEADERS
            if name in headers
        }

    query = getattr(request, "GET", None)
    if query is not None:
        context["queryKeys"] = sorted(query.keys())[:20]

    user = getattr(request, "user", None)
    if user is not None and getattr(user, "is_authenticated", False):
        # An id, never a name or an address: enough to correlate, not enough to
        # be personal data in an error store.
        context["userId"] = str(getattr(user, "pk", ""))

    return scrub(context)
