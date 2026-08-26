"""
Handing a message to something that sends it.

An adapter, for the same reason `advanced-search` has one: the store's call
sites must not change when the provider does. Three of them, and the default is
the one that sends nothing.

**`recording` is the default, deliberately.** A marketing Feature that started
mailing customers the moment it was installed — before anybody had reviewed a
template or set a from-address — would be the single worst default in this
catalogue. Recording mode does everything except deliver: it writes the `Send`
row, stores the rendered body, and returns a synthetic id. A merchant sees
exactly what would have gone out, to whom, and switches the provider on when
they are ready.

**`django-mail`** uses whatever the store already configured for its own
transactional mail. Right for a shop with its own SMTP, and it needs no secret.

**`api`** is a hosted provider reached over HTTP with a key. The key is a
*named* secret — declared in the manifest without a value, delivered over the
install channel, and read through `config.secret()`. What this module does **not**
contain is a call to any particular vendor's API: that is a real integration
against a real account, and inventing one here would mean shipping code nobody
has ever seen work. It refuses clearly instead, which is the honest state of it
(`docs/phase-15-verification.md`).
"""

from __future__ import annotations

import hashlib
import logging
from dataclasses import dataclass

from . import config

logger = logging.getLogger(__name__)

RECORDING = "recording"
DJANGO_MAIL = "django-mail"
API = "api"

SUPPORTED = (RECORDING, DJANGO_MAIL, API)


@dataclass(frozen=True)
class Delivery:
    """
    What the provider did with one message.

    `message_id` is what the provider called it, for tracing a delivery through
    their logs. `delivered` is False for a refusal, and `detail` says why in
    words a support conversation can use.
    """

    delivered: bool
    message_id: str = ""
    detail: str = ""


class Provider:
    """The surface every provider offers. Deliberately one method."""

    name = "none"

    def send(self, *, to: str, subject: str, body: str, from_email: str) -> Delivery:
        raise NotImplementedError


class RecordingProvider(Provider):
    """
    Records what would be sent and sends nothing.

    The synthetic id is derived from the message rather than random, so the same
    message recorded twice is recognisably the same message — which is what
    somebody reading the send log is trying to work out.
    """

    name = RECORDING

    def send(self, *, to: str, subject: str, body: str, from_email: str) -> Delivery:
        digest = hashlib.sha256(f"{to}|{subject}|{body}".encode()).hexdigest()[:16]

        logger.info("Recording mode: would send '%s' to %s.", subject, to)

        return Delivery(
            delivered=True,
            message_id=f"recorded-{digest}",
            detail="recorded, not sent",
        )


class DjangoMailProvider(Provider):
    """
    Sends through whatever the store configured for its own mail.

    No secret needed: the store's SMTP settings are the store's own. A failure
    comes back as a refusal rather than an exception, because one bad address
    must not stop a campaign run.
    """

    name = DJANGO_MAIL

    def send(self, *, to: str, subject: str, body: str, from_email: str) -> Delivery:
        from django.core.mail import send_mail

        try:
            send_mail(
                subject=subject,
                message=body,
                from_email=from_email or None,
                recipient_list=[to],
                fail_silently=False,
            )
        except Exception as error:  # noqa: BLE001 - one bad address must not stop a run
            return Delivery(delivered=False, detail=str(error)[:400])

        # Django's mail API returns a count, not an id. Saying so beats inventing
        # something that looks like a provider reference and cannot be searched.
        return Delivery(delivered=True, message_id="", detail="sent via the store's mail backend")


class ApiProvider(Provider):
    """
    A hosted provider reached with an API key.

    The key comes from `config.secret()` — named in the manifest, valued nowhere
    in this repository, and delivered only to the store that needs it. This class
    exists to prove that path end to end and to refuse honestly when the key did
    not arrive.

    It does not call a vendor. See the module docstring.
    """

    name = API

    def send(self, *, to: str, subject: str, body: str, from_email: str) -> Delivery:
        key = config.secret(config.SECRET_API_KEY)

        if not key:
            return Delivery(
                delivered=False,
                detail=(
                    f"The '{config.SECRET_API_KEY}' secret has not been delivered to this store, "
                    "so the API provider cannot send."
                ),
            )

        # Never logged, never returned, never stored on the Send row. The only
        # thing that leaves here is that it was present and long enough to be
        # plausible.
        if len(key) < 16:
            return Delivery(
                delivered=False,
                detail=f"The '{config.SECRET_API_KEY}' secret is too short to be a valid key.",
            )

        return Delivery(
            delivered=False,
            detail=(
                "The API provider has a key but no vendor integration. Configure "
                "'django-mail', or add the vendor adapter before selecting 'api'."
            ),
        )


def current() -> Provider:
    """
    The provider this store is configured for.

    An unknown name falls back to `recording` and says so. Falling back to
    *sending* would mean a typo in a configuration value mailing a customer
    list, so the failure direction is chosen rather than accidental.
    """
    configured = str(config.value("provider", RECORDING)).lower()

    if configured == DJANGO_MAIL:
        return DjangoMailProvider()

    if configured == API:
        return ApiProvider()

    if configured != RECORDING:
        logger.error(
            "Unknown email provider '%s'; falling back to recording mode so that nothing is sent.",
            configured,
        )

    return RecordingProvider()
