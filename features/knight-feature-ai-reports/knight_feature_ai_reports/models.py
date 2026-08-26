"""
Automated interpretation of the analytics data, and what it cost to produce.

The commercial value here is not "AI" — it is **automated business
interpretation**: a merchant being told that Tuesday evenings convert badly
rather than being handed a chart and left to notice. That framing decides the
architecture, and the split is the most important thing in this package:

- **Findings are computed, not generated.** The deltas, the outliers and the
  trends come out of the analytics event stream by arithmetic. They are
  deterministic, auditable, free, and correct whether or not a model provider is
  reachable. A number a merchant acts on must never be something a language model
  produced.
- **Prose is generated, and optional.** A model turns the findings into a
  paragraph somebody wants to read. If the provider is absent, over budget, or
  broken, the findings still stand and the report says so.

That is also what makes the cost control meaningful. Something concrete is being
bought — narration — and it can be refused without the Feature stopping working.

**What leaves the store is aggregates and nothing else**: counts, sums and
percentages already computed here. No customer identifiers, no order contents,
no free text a shopper wrote
([`adr/0030`](../../../docs/adr/0030-what-store-data-may-reach-a-model-provider.md)).
"""

from decimal import Decimal

from django.core.exceptions import ValidationError
from django.core.validators import MinValueValidator
from django.db import models
from django.utils import timezone


class Period(models.TextChoices):
    DAY = "Day", "A day"
    WEEK = "Week", "A week"


class RunState(models.TextChoices):
    COMPUTED = "Computed", "Findings computed, not narrated"
    NARRATED = "Narrated", "Findings computed and narrated"
    REFUSED = "Refused", "Narration refused: over budget or unavailable"
    FAILED = "Failed", "The run could not complete"


class Severity(models.TextChoices):
    """
    How much a finding should worry somebody.

    Three levels rather than a score. A number invites a threshold argument, and
    the only question a merchant asks is whether to do something today.
    """

    INFO = "Info", "Worth knowing"
    NOTABLE = "Notable", "Worth looking at"
    URGENT = "Urgent", "Worth acting on"


class Budget(models.Model):
    """
    What this store may spend on narration, and what it has spent.

    One row, always. A cap rather than a warning: an AI Feature whose
    per-customer spend is unbounded is a commercial risk rather than a technical
    one, and a limit that only logs is not a limit.

    The window is the calendar month, because that is the unit a bill arrives in
    and the unit a merchant reasons about.
    """

    id = models.PositiveSmallIntegerField(primary_key=True, default=1)

    monthly_token_cap = models.PositiveIntegerField(default=200_000)
    monthly_cost_cap = models.DecimalField(
        max_digits=10, decimal_places=2, default=Decimal("20.00"),
        validators=[MinValueValidator(Decimal("0"))],
    )

    # Reset when the month rolls over rather than by a scheduled job: a counter
    # that depends on a job having run is a counter that is wrong after an
    # outage, and this one decides whether money is spent.
    #
    # localdate, not now. This is a DateField and timezone.now returns a
    # datetime; Django coerces it on save, so the mistake survives a round trip
    # and surfaces only on an unsaved instance - where isoformat() prints a time
    # nobody expected.
    window_started_on = models.DateField(default=timezone.localdate)
    tokens_used = models.PositiveIntegerField(default=0)
    cost_used = models.DecimalField(max_digits=10, decimal_places=2, default=Decimal("0"))

    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        db_table = "knight_ai_reports_budget"

    @classmethod
    def current(cls) -> "Budget":
        return cls.objects.get_or_create(id=1)[0]

    def rolled_over(self, today=None) -> bool:
        """Whether the calendar month has changed since the window opened."""
        moment = today or timezone.now().date()

        return (moment.year, moment.month) != (
            self.window_started_on.year,
            self.window_started_on.month,
        )

    def remaining_tokens(self) -> int:
        return max(0, self.monthly_token_cap - self.tokens_used)

    def remaining_cost(self) -> Decimal:
        return max(Decimal("0"), self.monthly_cost_cap - self.cost_used)

    def has_headroom(self, *, tokens: int, cost: Decimal) -> bool:
        return self.remaining_tokens() >= tokens and self.remaining_cost() >= cost

    def __str__(self) -> str:
        return f"{self.tokens_used}/{self.monthly_token_cap} tokens this month"


class Report(models.Model):
    """
    One period's findings, and the narration if there was any.

    Unique per period and date, so re-running a day produces the same report
    rather than a second one. A merchant comparing two reports for the same
    Tuesday would have no way to tell which was true.
    """

    period = models.CharField(max_length=8, choices=Period, default=Period.DAY)
    covers = models.DateField()

    state = models.CharField(max_length=12, choices=RunState, default=RunState.COMPUTED)

    # The generated paragraph. Blank when narration was refused or unavailable —
    # which is a state a merchant sees rather than an error.
    narrative = models.TextField(blank=True, default="")

    # Why there is no narrative, when there is none.
    narration_note = models.CharField(max_length=250, blank=True, default="")

    # What the narration cost, recorded per report rather than only in the
    # budget, so a merchant can see which reports were expensive.
    model_name = models.CharField(max_length=100, blank=True, default="")
    tokens_used = models.PositiveIntegerField(default=0)
    cost = models.DecimalField(max_digits=10, decimal_places=4, default=Decimal("0"))

    created_at = models.DateTimeField(default=timezone.now)

    class Meta:
        db_table = "knight_ai_report"
        ordering = ("-covers", "period")
        constraints = [
            models.UniqueConstraint(
                fields=["period", "covers"], name="knight_ai_one_report_per_period"
            ),
        ]

    @property
    def narrated(self) -> bool:
        return bool(self.narrative)

    def __str__(self) -> str:
        return f"{self.period} report for {self.covers}"


class Finding(models.Model):
    """
    One thing worth telling somebody, computed by arithmetic.

    `evidence` holds the numbers the finding was drawn from, so a merchant who
    disagrees can check it. A finding nobody can verify is an opinion, and this
    Feature does not sell opinions.
    """

    report = models.ForeignKey(Report, on_delete=models.CASCADE, related_name="findings")

    code = models.CharField(max_length=60)
    severity = models.CharField(max_length=10, choices=Severity, default=Severity.INFO)

    # Already a sentence. Written by the analysis rather than by a model,
    # because this is the part a merchant may act on.
    headline = models.CharField(max_length=300)

    evidence = models.JSONField(default=dict, blank=True)

    class Meta:
        db_table = "knight_ai_finding"
        ordering = ("report", "-severity", "code")

    def clean(self) -> None:
        super().clean()

        if not self.headline.strip():
            raise ValidationError({"headline": "A finding has to say something."})

    def __str__(self) -> str:
        return f"[{self.severity}] {self.headline}"
