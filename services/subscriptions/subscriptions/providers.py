"""
Taking the money, or honestly declining to.

The only part of this Feature that can move money or talk to anybody outside the
store. Everything above it decides *whether* a charge is owed; this decides
whether one can be made, and it is deliberately the smallest module in the
package.

Three providers, and the default is the one that charges nothing:

**`manual`** takes no money and says so. It is for the shop that bills by
standing order, invoice or bank transfer and wants KNIGHT to keep the schedule,
the periods and the ledger — which is most of the value of a subscriptions
Feature and none of the risk. A period charged by `manual` is marked paid because
a human said it was, and the attempt records that a human said it.

**`none`** refuses everything, by name. It is what a store gets if it activates a
subscription before configuring a provider, and refusing loudly is the entire
point: the alternative is a Feature that reports success and moves no money,
which a merchant discovers a month later when nobody has been charged.

**`api`** is a real payment provider, and this module contains no call to a
particular one. Wiring a vendor is an integration against a real account under a
real agreement, and inventing it here would mean shipping code nobody has watched
work — the same call `marketing-automation` and `ai-reports` made in phase 15,
and for the same reason. The path up to and including the refusal is real: the
credential is a named secret delivered over the install channel, it is validated,
and it is never returned, logged or put in an error message.

**Nothing here ever sees a card.** The provider is handed an amount and a token
the provider itself issued. There is no parameter a card number would fit in and
no field to put one in, which is what keeps a store that installs this out of a
compliance regime it did not choose.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from decimal import Decimal

from . import config
from .models import AttemptOutcome

logger = logging.getLogger(__name__)

MANUAL = "manual"
NONE = "none"
API = "api"


@dataclass(frozen=True)
class Result:
    """What one attempt to take money came back with."""

    outcome: str
    detail: str = ""
    provider_reference: str = ""


def charge(
    *,
    provider: str,
    amount: Decimal,
    currency: str,
    reference: str,
    payment_method_reference: str = "",
) -> Result:
    """
    Attempts one charge.

    Never raises. A provider that threw would leave the caller having to decide
    whether the money moved, and "we do not know" is the one answer a billing
    system may not give — so every path here returns a Result, and an unexpected
    exception becomes a recorded failure rather than an unrecorded one.
    """
    try:
        if provider == MANUAL:
            return _manual(amount, currency, reference)

        if provider == API:
            return _api(amount, currency, reference, payment_method_reference)

        return _none(provider)
    except Exception as exc:  # noqa: BLE001 - see the docstring
        # Logged without the amount or the reference: a provider failure is worth
        # a stack trace and a shopper's payment details are not worth writing
        # down anywhere, including here.
        logger.exception("The '%s' provider raised while charging.", provider)

        return Result(
            outcome=AttemptOutcome.FAILED,
            detail=f"The '{provider}' provider raised: {type(exc).__name__}.",
        )


def _manual(amount: Decimal, currency: str, reference: str) -> Result:
    """
    Records the period as settled without moving money.

    For a shop that bills by standing order or invoice. The attempt row says
    `manual` so that nobody reading the ledger later mistakes it for a card
    payment — the money is real, the mechanism is a person, and the record has to
    say which.
    """
    return Result(
        outcome=AttemptOutcome.SUCCEEDED,
        detail=f"Recorded as settled outside the store: {amount} {currency}.",
        provider_reference=f"manual:{reference}",
    )


def _none(provider: str) -> Result:
    """
    Refuses, by name.

    A refusal rather than a failure, because nothing was declined — nobody was
    asked. The distinction is what stops a merchant reading their report as "our
    payments are being rejected" when the truth is "we never configured a
    provider".
    """
    if provider not in {NONE, ""}:
        return Result(
            outcome=AttemptOutcome.REFUSED,
            detail=f"'{provider}' is not a payment provider this Feature knows.",
        )

    return Result(
        outcome=AttemptOutcome.REFUSED,
        detail=(
            "No payment provider is configured, so nothing was charged. "
            "Set `provider` and the payment secret in this Feature's configuration."
        ),
    )


def _api(amount: Decimal, currency: str, reference: str, payment_method_reference: str) -> Result:
    """
    The shape of a real payment integration, refusing at the point the call would
    be made.

    Everything before the vendor call is real and is what a wiring-up would keep:
    the credential is read from the delivered secrets, its absence is a refusal
    rather than a crash, and a charge with no payment method is refused before a
    provider could decline it — which is worth doing here because a declined
    charge counts against a shopper's card and a refused one does not.
    """
    secret = config.secret("payment_api_key")

    if not secret:
        return Result(
            outcome=AttemptOutcome.REFUSED,
            detail=(
                "The 'api' provider needs the `payment_api_key` secret, which this store "
                "has not been given. Nothing was charged."
            ),
        )

    if not payment_method_reference:
        return Result(
            outcome=AttemptOutcome.REFUSED,
            detail="The subscription has no payment method on file, so nothing was attempted.",
        )

    # Deliberately not implemented. See the module docstring: which provider,
    # under what agreement, in which jurisdiction, is a commercial decision, and
    # a plausible-looking HTTP call to a vendor nobody has an account with would
    # be worse than this sentence.
    return Result(
        outcome=AttemptOutcome.REFUSED,
        detail=(
            f"The 'api' provider is configured but not wired to a vendor, so {amount} {currency} "
            f"for {reference} was not charged."
        ),
    )
