"""
Turning computed findings into a paragraph, and counting what it cost.

The narration layer, and the only part of this Feature that can spend money or
talk to anybody outside the store. Everything it is allowed to send has already
been computed by `analysis` and is aggregate-only
([`adr/0030`](../../../docs/adr/0030-what-store-data-may-reach-a-model-provider.md)).

Two providers:

**`local` is the default and needs nothing.** It assembles the findings into
readable prose by template. No provider, no key, no cost, no data leaving the
store — and a merchant on the default configuration still gets a report they can
read. That is the honest default for a Feature sold on interpretation: the
interpretation is the findings, and the model only changes the register.

**`api`** sends the findings to a model provider. The key is a *named* secret,
the payload is built by `redact()` and nothing else, and every call is priced
before it is made so the budget can refuse it.

As with `marketing-automation`, this module contains no call to a particular
vendor. Wiring one is a real integration against a real account; inventing it
here would mean shipping code nobody has watched work. The path up to and
including the refusal is real and tested.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from decimal import Decimal

from . import config

logger = logging.getLogger(__name__)

LOCAL = "local"
API = "api"

#: Rough price per 1,000 tokens, in the store's currency. Configurable, because
#: it changes and because a store on a negotiated rate should be able to say so.
#: Used to price a call *before* making it, which is what lets the budget refuse.
DEFAULT_PRICE_PER_1K = Decimal("0.01")

#: How many tokens a call is assumed to cost, before it is made. Deliberately
#: generous: an estimate that ran under budget and then billed over it would
#: make the cap a suggestion.
TOKENS_PER_FINDING = 120
TOKENS_OVERHEAD = 400


@dataclass(frozen=True)
class Narration:
    """What a narration attempt produced, and what it cost."""

    text: str = ""
    model: str = ""
    tokens: int = 0
    cost: Decimal = Decimal("0")
    refused: bool = False
    detail: str = ""

    @property
    def produced(self) -> bool:
        return bool(self.text) and not self.refused


def estimate_tokens(findings) -> int:
    """
    What a narration of these findings will cost, before it is attempted.

    Estimated up front rather than measured afterwards, because a budget that
    can only be checked after the money is spent is not a budget.
    """
    return TOKENS_OVERHEAD + TOKENS_PER_FINDING * len(findings)


def price(tokens: int) -> Decimal:
    per_1k = Decimal(str(config.value("price_per_1k_tokens", DEFAULT_PRICE_PER_1K)))

    return (Decimal(tokens) / Decimal(1000) * per_1k).quantize(Decimal("0.0001"))


def redact(findings) -> list[dict]:
    """
    The only thing that may ever leave the store.

    Aggregates that `analysis` already computed: a code, a severity, a sentence
    it wrote, and the numbers behind it. No customer identifiers, no order
    contents, no free text a shopper wrote.

    Built by allow-list rather than by removing known-bad keys. A deny-list is
    one new field away from leaking, and the new field is always added by
    somebody who is not thinking about this file
    (adr/0030).
    """
    allowed_evidence = {
        "current",
        "baseline",
        "change",
        "orders",
        "revenue",
        "customers",
        "largestShare",
    }

    return [
        {
            "code": finding.code,
            "severity": finding.severity,
            "headline": finding.headline,
            "evidence": {
                key: value
                for key, value in (finding.evidence or {}).items()
                if key in allowed_evidence
            },
        }
        for finding in findings
    ]


class Provider:
    name = "none"

    def narrate(self, findings, *, period: str, covers) -> Narration:
        raise NotImplementedError


class LocalProvider(Provider):
    """
    Assembles the findings into prose without leaving the store.

    Not a fallback that produces something worse — for most reports this is what
    a merchant wants: the findings in order of urgency, in sentences. It costs
    nothing and sends nothing.
    """

    name = LOCAL

    def narrate(self, findings, *, period: str, covers) -> Narration:
        if not findings:
            return Narration(text="", detail="there was nothing to report")

        order = {"Urgent": 0, "Notable": 1, "Info": 2}
        ranked = sorted(findings, key=lambda finding: order.get(finding.severity, 3))

        urgent = [finding for finding in ranked if finding.severity == "Urgent"]

        opening = (
            f"The {period.lower()} of {covers} needs attention."
            if urgent
            else f"The {period.lower()} of {covers} looks ordinary."
        )

        lines = [opening, ""]
        lines.extend(f"- {finding.headline}" for finding in ranked)

        if urgent:
            lines.append("")
            lines.append("The first item is the one to look at today.")

        return Narration(text="\n".join(lines), model=LOCAL, tokens=0, cost=Decimal("0"))


class ApiProvider(Provider):
    """
    Sends the redacted findings to a model provider.

    Refuses clearly when the key did not arrive, and never puts the key into a
    message anybody will read. See the module docstring for why there is no
    vendor call here.
    """

    name = API

    def narrate(self, findings, *, period: str, covers) -> Narration:
        key = config.secret(config.SECRET_API_KEY)

        if not key:
            return Narration(
                refused=True,
                detail=(
                    f"The '{config.SECRET_API_KEY}' secret has not been delivered to this store, "
                    "so narration is unavailable. The findings below were computed locally."
                ),
            )

        payload = redact(findings)
        tokens = estimate_tokens(findings)

        logger.info(
            "Would narrate %s finding(s) for %s, about %s tokens.", len(payload), covers, tokens
        )

        return Narration(
            refused=True,
            detail=(
                "The API provider has a key but no vendor integration. Configure 'local', "
                "or add the vendor adapter before selecting 'api'."
            ),
        )


def current() -> Provider:
    """
    The provider this store is configured for.

    An unknown name falls back to `local`, which sends nothing. The failure
    direction is chosen rather than accidental: a typo in a configuration value
    must not be what sends a store's figures to a third party.
    """
    configured = str(config.value("provider", LOCAL)).lower()

    if configured == API:
        return ApiProvider()

    if configured != LOCAL:
        logger.error(
            "Unknown narration provider '%s'; falling back to local so nothing leaves the store.",
            configured,
        )

    return LocalProvider()
