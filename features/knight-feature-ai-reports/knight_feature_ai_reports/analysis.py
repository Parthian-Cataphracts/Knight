"""
Turning an event stream into findings, by arithmetic.

Everything in this module is deterministic. Given the same events it produces
the same findings, every finding carries the numbers it was drawn from, and none
of it needs a model provider or costs anything.

That is deliberate and it is the whole design: a merchant may act on these, so
they must be checkable. The language model's job is to read them aloud, and it
is only ever given what this module has already computed
([`adr/0030`](../../../docs/adr/0030-what-store-data-may-reach-a-model-provider.md)).

The comparison is always **this period against the ones before it**, because
"12% down" is a fact a shop can act on and "1,200 in revenue" is a number it
already had.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date, timedelta
from decimal import Decimal
from statistics import mean
from typing import Any

#: How many prior periods a comparison is drawn from. Four weeks of weekdays is
#: enough for a Tuesday to be compared against Tuesdays; fewer and one unusual
#: day sets the baseline.
BASELINE_PERIODS = 4

#: A change smaller than this is noise, not news. A report that flagged every
#: 3% wobble would train its reader to ignore it.
MATERIAL_CHANGE = Decimal("0.10")

#: Above this, the finding is urgent rather than merely notable.
SEVERE_CHANGE = Decimal("0.25")

ORDER_EVENT = "order.placed"


@dataclass
class Finding:
    """One computed observation, before it is written to the database."""

    code: str
    headline: str
    severity: str = "Info"
    evidence: dict[str, Any] = field(default_factory=dict)


def _percent(change: Decimal) -> str:
    return f"{abs(change) * 100:.0f}%"


def _severity(change: Decimal) -> str:
    if abs(change) >= SEVERE_CHANGE:
        return "Urgent"

    return "Notable"


def _ratio(current: Decimal, baseline: Decimal) -> Decimal | None:
    """
    The proportional change, or None when there is no baseline to compare with.

    None rather than zero or infinity: a shop's first week has nothing to be
    down against, and reporting "0% change" for it would be a lie in a place a
    merchant is looking for one.
    """
    if baseline <= Decimal("0"):
        return None

    return ((current - baseline) / baseline).quantize(Decimal("0.0001"))


def _totals_for(analytics, day: date) -> tuple[int, Decimal]:
    """Orders and their value for one day, from the analytics service surface."""
    from datetime import datetime, time, timezone

    start = datetime.combine(day, time.min, tzinfo=timezone.utc)
    rows = analytics.subjects_between(start, start + timedelta(days=1), name=ORDER_EVENT)

    orders = sum(row.events for row in rows)
    value = sum((row.total_value for row in rows), Decimal("0"))

    return orders, Decimal(value).quantize(Decimal("0.01"))


def compute(analytics, *, covers: date, period: str = "Day") -> list[Finding]:
    """
    Every finding for one period.

    `analytics` is passed in rather than imported here so this module can be
    reasoned about — and tested — without a database or an installed dependency.
    """
    length = 7 if period == "Week" else 1
    findings: list[Finding] = []

    current_orders, current_value = _period_totals(analytics, covers, length)

    baseline = [
        _period_totals(analytics, covers - timedelta(days=length * step), length)
        for step in range(1, BASELINE_PERIODS + 1)
    ]

    baseline_orders = [orders for orders, _ in baseline]
    baseline_values = [value for _, value in baseline]

    if current_orders == 0 and not any(baseline_orders):
        return [
            Finding(
                code="no-activity",
                headline="No orders were recorded in this period or the ones before it.",
                severity="Info",
                evidence={"orders": 0},
            )
        ]

    findings.extend(_volume(current_orders, baseline_orders))
    findings.extend(_revenue(current_value, baseline_values))
    findings.extend(_basket(current_orders, current_value, baseline_orders, baseline_values))
    findings.extend(_customers(analytics, covers, length))

    if not findings:
        findings.append(
            Finding(
                code="steady",
                headline="Nothing moved enough to be worth reporting. The period was steady.",
                severity="Info",
                evidence={"orders": current_orders, "revenue": str(current_value)},
            )
        )

    return findings


def _period_totals(analytics, ending: date, length: int) -> tuple[int, Decimal]:
    orders = 0
    value = Decimal("0")

    for offset in range(length):
        day_orders, day_value = _totals_for(analytics, ending - timedelta(days=offset))
        orders += day_orders
        value += day_value

    return orders, value


def _volume(current: int, baseline: list[int]) -> list[Finding]:
    if not baseline or not any(baseline):
        return []

    average = Decimal(str(mean(baseline))).quantize(Decimal("0.01"))
    change = _ratio(Decimal(current), average)

    if change is None or abs(change) < MATERIAL_CHANGE:
        return []

    direction = "up" if change > 0 else "down"

    return [
        Finding(
            code="order-volume",
            severity=_severity(change),
            headline=(
                f"Orders are {direction} {_percent(change)} on the recent average "
                f"({current} against {average})."
            ),
            evidence={"current": current, "baseline": str(average), "change": str(change)},
        )
    ]


def _revenue(current: Decimal, baseline: list[Decimal]) -> list[Finding]:
    if not baseline or not any(baseline):
        return []

    average = Decimal(str(mean(baseline))).quantize(Decimal("0.01"))
    change = _ratio(current, average)

    if change is None or abs(change) < MATERIAL_CHANGE:
        return []

    direction = "up" if change > 0 else "down"

    return [
        Finding(
            code="revenue",
            severity=_severity(change),
            headline=(
                f"Revenue is {direction} {_percent(change)} on the recent average "
                f"({current} against {average})."
            ),
            evidence={"current": str(current), "baseline": str(average), "change": str(change)},
        )
    ]


def _basket(
    current_orders: int,
    current_value: Decimal,
    baseline_orders: list[int],
    baseline_values: list[Decimal],
) -> list[Finding]:
    """
    Average order value, which moves for different reasons than volume does.

    Worth its own finding: revenue down with volume flat is a pricing or
    basket-size problem, and revenue down with volume down is a traffic problem.
    A merchant told only about revenue cannot tell those apart.
    """
    if current_orders == 0:
        return []

    prior_orders = sum(baseline_orders)
    prior_value = sum(baseline_values, Decimal("0"))

    if prior_orders == 0:
        return []

    current_basket = (current_value / current_orders).quantize(Decimal("0.01"))
    baseline_basket = (prior_value / prior_orders).quantize(Decimal("0.01"))
    change = _ratio(current_basket, baseline_basket)

    if change is None or abs(change) < MATERIAL_CHANGE:
        return []

    direction = "up" if change > 0 else "down"

    return [
        Finding(
            code="average-order-value",
            severity=_severity(change),
            headline=(
                f"The average order is {direction} {_percent(change)} "
                f"({current_basket} against {baseline_basket})."
            ),
            evidence={
                "current": str(current_basket),
                "baseline": str(baseline_basket),
                "change": str(change),
            },
        )
    ]


def _customers(analytics, covers: date, length: int) -> list[Finding]:
    """
    How concentrated the period's revenue was.

    A day whose takings came from one customer is a different day from one with
    forty, and the totals cannot tell them apart. This is the finding that most
    often explains an otherwise inexplicable spike.
    """
    from datetime import datetime, time, timezone

    end = datetime.combine(covers + timedelta(days=1), time.min, tzinfo=timezone.utc)
    start = end - timedelta(days=length)

    rows = analytics.subjects_between(start, end, name=ORDER_EVENT)

    if len(rows) < 2:
        return []

    total = sum((row.total_value for row in rows), Decimal("0"))

    if total <= Decimal("0"):
        return []

    largest = max(rows, key=lambda row: row.total_value)
    share = _ratio(largest.total_value, total)

    if share is None:
        return []

    proportion = (largest.total_value / total).quantize(Decimal("0.0001"))

    if proportion < Decimal("0.5"):
        return []

    return [
        Finding(
            code="revenue-concentration",
            severity="Notable",
            headline=(
                f"{_percent(proportion)} of the period's revenue came from a single customer, "
                f"across {len(rows)} customers in total."
            ),
            # The subject is deliberately absent. A finding that named the
            # customer would be a customer identifier in a document that may be
            # sent to a model provider (adr/0030).
            evidence={"customers": len(rows), "largestShare": str(proportion)},
        )
    ]
