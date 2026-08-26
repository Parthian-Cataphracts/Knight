"""
Generating a report, and refusing to spend more than the store agreed to.

The order of operations is the design:

1. **Compute the findings.** Arithmetic over the analytics event stream. Free,
   deterministic, and the part a merchant may act on.
2. **Price the narration before attempting it.** An estimate up front, because a
   budget that can only be checked after the money is spent is not a budget.
3. **Ask the budget.** Over cap means no narration, recorded as a refusal the
   merchant can see — not an error, and never a silent overspend.
4. **Narrate, and record what it cost.**

A report always exists. Narration is the optional half, so a store that is over
budget, has no key, or cannot reach a provider still gets its findings.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from datetime import date, timedelta
from decimal import Decimal

from django.db import transaction
from django.utils import timezone

from . import analysis, config, providers
from .models import Budget, Finding, Period, Report, RunState

logger = logging.getLogger(__name__)


class DependencyUnavailable(RuntimeError):
    """
    Raised when `analytics-core` is missing or too old.

    Explicit rather than an empty report. A report with no findings because the
    event stream was unreachable looks exactly like a quiet week, and a merchant
    would believe it.
    """


@dataclass(frozen=True)
class GenerateResult:
    report: Report
    findings: int
    narrated: bool
    refused_reason: str = ""


def _analytics():
    try:
        from knight_feature_analytics_core import services as analytics_services
    except ImportError as error:  # pragma: no cover - depends on the install
        raise DependencyUnavailable(
            "analytics-core is not installed on this store, and ai-reports has nothing to read."
        ) from error

    if not hasattr(analytics_services, "subjects_between"):
        raise DependencyUnavailable(
            "analytics-core on this store is older than 1.1.0 and cannot group events by subject."
        )

    return analytics_services


# --- Budget -----------------------------------------------------------------


def budget() -> Budget:
    """
    The store's spending window, rolled over if the month has changed.

    Rolled over on read rather than by a scheduled job. A counter that depends
    on a job having run is a counter that is wrong after an outage, and this one
    decides whether money is spent.
    """
    record = Budget.current()
    token_cap, cost_cap = config.caps()

    changed = []

    if record.rolled_over():
        record.window_started_on = timezone.now().date().replace(day=1)
        record.tokens_used = 0
        record.cost_used = Decimal("0")
        changed += ["window_started_on", "tokens_used", "cost_used"]

    # The caps follow the configuration, so raising a customer's limit is a
    # configuration change delivered over the install channel rather than a
    # database edit.
    if record.monthly_token_cap != token_cap:
        record.monthly_token_cap = token_cap
        changed.append("monthly_token_cap")

    if record.monthly_cost_cap != cost_cap:
        record.monthly_cost_cap = cost_cap
        changed.append("monthly_cost_cap")

    if changed:
        record.save(update_fields=[*changed, "updated_at"])

    return record


@transaction.atomic
def _spend(tokens: int, cost: Decimal) -> None:
    """Records spend against the window, with the row locked."""
    record = Budget.objects.select_for_update().get(pk=1)
    record.tokens_used += tokens
    record.cost_used += cost
    record.save(update_fields=["tokens_used", "cost_used", "updated_at"])


def usage() -> dict:
    """What has been spent this month, for an operator and for a bill."""
    record = budget()

    return {
        "windowStartedOn": record.window_started_on.isoformat(),
        "tokensUsed": record.tokens_used,
        "tokenCap": record.monthly_token_cap,
        "tokensRemaining": record.remaining_tokens(),
        "costUsed": str(record.cost_used),
        "costCap": str(record.monthly_cost_cap),
        "costRemaining": str(record.remaining_cost()),
    }


# --- Generating -------------------------------------------------------------


@transaction.atomic
def generate(*, covers: date | None = None, period: str = Period.DAY) -> GenerateResult:
    """
    Produces one report, replacing any earlier one for the same period.

    Replaced rather than added to: a merchant comparing two reports for the same
    Tuesday would have no way to tell which was true.
    """
    day = covers or (timezone.now().date() - timedelta(days=1))

    findings = analysis.compute(_analytics(), covers=day, period=period)

    report, _ = Report.objects.update_or_create(
        period=period,
        covers=day,
        defaults={
            "state": RunState.COMPUTED,
            "narrative": "",
            "narration_note": "",
            "model_name": "",
            "tokens_used": 0,
            "cost": Decimal("0"),
            "created_at": timezone.now(),
        },
    )

    report.findings.all().delete()

    Finding.objects.bulk_create(
        [
            Finding(
                report=report,
                code=finding.code,
                severity=finding.severity,
                headline=finding.headline[:300],
                evidence=finding.evidence,
            )
            for finding in findings
        ]
    )

    narration = _narrate(findings, period=period, covers=day)

    if narration.produced:
        report.state = RunState.NARRATED
        report.narrative = narration.text
        report.model_name = narration.model
        report.tokens_used = narration.tokens
        report.cost = narration.cost

        if narration.tokens or narration.cost:
            _spend(narration.tokens, narration.cost)
    else:
        report.state = RunState.REFUSED
        report.narration_note = narration.detail[:250]

    report.save(
        update_fields=[
            "state",
            "narrative",
            "narration_note",
            "model_name",
            "tokens_used",
            "cost",
        ]
    )

    return GenerateResult(
        report=report,
        findings=len(findings),
        narrated=narration.produced,
        refused_reason="" if narration.produced else narration.detail,
    )


def _narrate(findings, *, period: str, covers: date) -> providers.Narration:
    """
    Narrates within budget, or refuses and says why.

    The budget is asked *before* the provider, so an over-cap store never makes
    the call at all. Refusing after the fact would be a limit that costs money
    to enforce.
    """
    provider = providers.current()

    if provider.name == providers.LOCAL:
        # Costs nothing and sends nothing, so there is nothing to ask about.
        return provider.narrate(findings, period=period, covers=covers)

    tokens = providers.estimate_tokens(findings)
    cost = providers.price(tokens)
    record = budget()

    if not record.has_headroom(tokens=tokens, cost=cost):
        logger.warning(
            "Narration refused for %s: it would need %s tokens and %s, leaving %s tokens and %s.",
            covers,
            tokens,
            cost,
            record.remaining_tokens(),
            record.remaining_cost(),
        )

        return providers.Narration(
            refused=True,
            detail=(
                f"This month's narration budget is spent ({record.tokens_used} of "
                f"{record.monthly_token_cap} tokens). The findings below were computed locally."
            ),
        )

    return provider.narrate(findings, period=period, covers=covers)


def generate_daily() -> list[dict]:
    """
    The worker's entrypoint: yesterday's report.

    Yesterday rather than today, because a report for a day that is still
    happening is a report that changes under whoever is reading it.
    """
    result = generate()

    return [
        {
            "covers": result.report.covers.isoformat(),
            "findings": result.findings,
            "narrated": result.narrated,
            "note": result.refused_reason,
        }
    ]


# --- Reading ----------------------------------------------------------------


def latest(*, period: str = Period.DAY) -> dict | None:
    """The most recent report, as a storefront or dashboard would read it."""
    report = Report.objects.filter(period=period).order_by("-covers").first()

    return None if report is None else describe(report)


def describe(report: Report) -> dict:
    return {
        "period": report.period,
        "covers": report.covers.isoformat(),
        "state": report.state,
        "narrative": report.narrative,
        "narrationNote": report.narration_note,
        "model": report.model_name,
        "tokensUsed": report.tokens_used,
        "cost": str(report.cost),
        "findings": [
            {
                "code": finding.code,
                "severity": finding.severity,
                "headline": finding.headline,
                "evidence": finding.evidence,
            }
            for finding in report.findings.all()
        ],
    }


def history(*, limit: int = 30) -> list[dict]:
    """Recent reports, newest first, without their findings."""
    rows = Report.objects.order_by("-covers")[: max(1, min(limit, 180))]

    return [
        {
            "period": row.period,
            "covers": row.covers.isoformat(),
            "state": row.state,
            "tokensUsed": row.tokens_used,
            "cost": str(row.cost),
        }
        for row in rows
    ]
