"""
The decorator every endpoint on this service wears.

It does two things and it is important that they are two things:

- **authenticates the store**, cryptographically, from the signature;
- **reads the identity the store asserted**, which is *not* authentication.

The second is worth being pedantic about. ``X-Knight-Identity`` and
``X-Knight-Subject`` say who the store believes is asking. They are believed
only because the signature over the whole request — headers included in the
canonical string via the path and body, and the headers themselves covered by
the store's own construction — has already verified. An unsigned request naming
a customer is an unauthenticated request naming a customer, and this service
never sees one, because the signature is checked first and a failure never
reaches the view (``adr/0033``).
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from functools import wraps

from django.http import JsonResponse

from .signing import Unsigned, verify


@dataclass(frozen=True)
class Caller:
    """Who the store says is asking, once the store itself has been proven."""

    store: object
    identity: str
    subject: str

    @property
    def is_staff(self) -> bool:
        return self.identity == "staff"

    @property
    def is_customer(self) -> bool:
        return self.identity == "customer"

    @property
    def is_anonymous(self) -> bool:
        return not self.subject or self.identity == "anonymous"


def signed(view=None, *, require: str | None = None):
    """
    Refuses anything the store did not sign, and optionally anything the store
    did not say was a customer or a member of staff.

    ``require`` is checked here as well as in the store, and that duplication is
    deliberate. The store enforces it so a shopper cannot reach a staff route;
    this enforces it so a store with a mis-wired proxy cannot either. Two
    independent checks of the same rule is the arrangement worth having when one
    of the two is somebody else's code.
    """

    def decorate(function):
        @wraps(function)
        def wrapper(request, *args, **kwargs):
            try:
                store = verify(request)
            except Unsigned as refusal:
                return JsonResponse(
                    {"detail": refusal.reason, "errorCode": refusal.code},
                    status=401,
                )

            caller = Caller(
                store=store,
                identity=request.headers.get("X-Knight-Identity", "anonymous"),
                subject=request.headers.get("X-Knight-Subject", ""),
            )

            if require == "staff" and not caller.is_staff:
                return JsonResponse(
                    {"detail": "This route is for staff.", "errorCode": "forbidden"},
                    status=403,
                )

            if require == "customer" and caller.is_anonymous:
                return JsonResponse(
                    {"detail": "This route needs a signed-in shopper.", "errorCode": "forbidden"},
                    status=403,
                )

            request.knight = caller

            # Every read of this Feature's configuration inside the block below
            # is this store's. A context manager rather than a setter, because
            # forgetting to unset would mean one store billed under another's
            # provider — found by a merchant rather than by a test.
            from subscriptions import config

            with config.use(store):
                return function(request, *args, **kwargs)

        return wrapper

    return decorate(view) if view else decorate


def body(request) -> dict:
    """
    The request's JSON body, or an empty map.

    Never raises. A malformed body from a store is a 400 the view decides on,
    not a 500 that looks like this service is broken.
    """
    if not request.body:
        return {}

    try:
        parsed = json.loads(request.body.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        return {}

    return parsed if isinstance(parsed, dict) else {}
