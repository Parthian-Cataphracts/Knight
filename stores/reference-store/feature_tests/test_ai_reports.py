"""
`ai-reports`, installed.

Two things carry most of the weight here, and both are the reason this Feature
is shaped the way it is:

**The findings are arithmetic.** They are deterministic, they carry their own
evidence, and they are correct with no provider configured. Most of these tests
are ordinary numeric assertions, which is the point — a merchant may act on
these numbers, so they must be checkable.

**The narration is optional and capped.** The budget is asked *before* the
provider, an over-cap store never makes the call, and a refusal still produces a
report. And nothing that could identify a customer is allowed into the payload
that may leave the store
([`adr/0030`](../../../docs/adr/0030-what-store-data-may-reach-a-model-provider.md)).
"""

# Lazy annotations, and not a style preference. `_codes(self, report: Report)`
# names a type that only exists when the Feature is installed, and Python
# evaluates an annotation at function-definition time - so without this the
# module fails to import on a base store rather than skipping. Caught by running
# the suite with no Features installed, which is what that configuration is for.
from __future__ import annotations

from datetime import date, datetime, timedelta, timezone as dt_timezone
from decimal import Decimal
from unittest import skipUnless
from unittest.mock import patch

from django.test import TestCase

from feature_tests.support import installed, require

APP = "knight_feature_ai_reports"
ANALYTICS = "knight_feature_analytics_core"
INSTALLED = installed(APP) and installed(ANALYTICS)
require(APP, ANALYTICS)

if INSTALLED:  # pragma: no cover - guarded above
    from knight_feature_analytics_core import services as analytics
    from knight_feature_ai_reports import analysis, config, providers, services
    from knight_feature_ai_reports.models import Budget, Period, Report, RunState

#: A configuration that selects the paid provider and has its key. Patched in
#: rather than written to disk, so a test can never leave a store pointed at a
#: provider.
API_CONFIG = {
    "version": 2,
    "values": {"provider": "api"},
    "secrets": {"model_api_key": "a-real-looking-key-0123456789abcd"},
}


@skipUnless(INSTALLED, "ai-reports and analytics-core are not installed.")
class ReportTestCase(TestCase):
    def setUp(self):
        self.yesterday = date.today() - timedelta(days=1)

    @staticmethod
    def _at(day: date, hour: int) -> datetime:
        return datetime(day.year, day.month, day.day, hour, tzinfo=dt_timezone.utc)

    def _orders(self, day: date, count: int, *, value: int = 100, customers: int | None = None):
        """`count` orders on `day`, spread across `customers` distinct subjects."""
        spread = customers if customers is not None else count

        for index in range(count):
            analytics.record(
                "order.placed",
                {"value": value},
                occurred_at=self._at(day, 9 + index % 8),
                subject=f"c{index % max(1, spread)}",
            )

    def _baseline(self, *, per_day: int = 10, value: int = 100, days: int = 4):
        for step in range(1, days + 1):
            self._orders(self.yesterday - timedelta(days=step), per_day, value=value)

    def _generate(self, **kwargs):
        return services.generate(covers=self.yesterday, **kwargs)

    def _codes(self, report: Report) -> set[str]:
        return {finding.code for finding in report.findings.all()}


class FindingTests(ReportTestCase):
    def test_a_material_drop_in_orders_is_reported_as_urgent(self):
        self._baseline(per_day=10)
        self._orders(self.yesterday, 4)

        result = self._generate()

        self.assertIn("order-volume", self._codes(result.report))
        volume = result.report.findings.get(code="order-volume")
        self.assertEqual(volume.severity, "Urgent")
        self.assertIn("down", volume.headline)

    def test_a_small_wobble_is_not_reported_at_all(self):
        # A report that flagged every 3% change would train its reader to ignore
        # it.
        self._baseline(per_day=10)
        self._orders(self.yesterday, 10)

        self.assertNotIn("order-volume", self._codes(self._generate().report))

    def test_a_rise_is_reported_as_well_as_a_fall(self):
        self._baseline(per_day=4)
        self._orders(self.yesterday, 12)

        self.assertIn("up", self._generate().report.findings.get(code="order-volume").headline)

    def test_every_finding_carries_the_numbers_it_was_drawn_from(self):
        # A finding nobody can verify is an opinion, and this Feature does not
        # sell opinions.
        self._baseline(per_day=10)
        self._orders(self.yesterday, 4)

        for finding in self._generate().report.findings.all():
            self.assertTrue(finding.evidence, finding.code)

    def test_average_order_value_is_reported_separately_from_revenue(self):
        # Revenue down with volume flat is a pricing problem; revenue down with
        # volume down is a traffic problem. A merchant told only about revenue
        # cannot tell them apart.
        self._baseline(per_day=10, value=100)
        self._orders(self.yesterday, 10, value=40)

        codes = self._codes(self._generate().report)

        self.assertIn("revenue", codes)
        self.assertIn("average-order-value", codes)
        self.assertNotIn("order-volume", codes)

    def test_revenue_concentrated_on_one_customer_is_called_out(self):
        # The finding that most often explains an otherwise inexplicable spike.
        self._baseline(per_day=10)
        analytics.record(
            "order.placed", {"value": 100000}, occurred_at=self._at(self.yesterday, 10), subject="whale"
        )
        analytics.record(
            "order.placed", {"value": 100}, occurred_at=self._at(self.yesterday, 11), subject="minnow"
        )

        self.assertIn("revenue-concentration", self._codes(self._generate().report))

    def test_the_concentration_finding_never_names_the_customer(self):
        # A finding carrying a customer reference would be a customer identifier
        # sitting in a record designed to be sent onward (adr/0030).
        self._baseline(per_day=10)
        analytics.record(
            "order.placed", {"value": 100000}, occurred_at=self._at(self.yesterday, 10), subject="whale"
        )
        analytics.record(
            "order.placed", {"value": 100}, occurred_at=self._at(self.yesterday, 11), subject="minnow"
        )

        finding = self._generate().report.findings.get(code="revenue-concentration")

        self.assertNotIn("whale", finding.headline)
        self.assertNotIn("whale", str(finding.evidence))

    def test_a_store_with_no_activity_says_so_rather_than_reporting_nothing(self):
        result = self._generate()

        self.assertEqual(self._codes(result.report), {"no-activity"})

    def test_a_steady_period_says_so_rather_than_being_empty(self):
        # An empty report reads as broken. "Nothing moved" is an answer.
        self._baseline(per_day=10)
        self._orders(self.yesterday, 10)

        self.assertEqual(self._codes(self._generate().report), {"steady"})

    def test_a_first_period_with_no_baseline_reports_no_change(self):
        # A shop's first day has nothing to be down against, and reporting "0%
        # change" for it would be a lie in a place a merchant is looking for one.
        self._orders(self.yesterday, 5)

        codes = self._codes(self._generate().report)

        self.assertNotIn("order-volume", codes)
        self.assertNotIn("revenue", codes)

    def test_the_findings_are_deterministic(self):
        self._baseline(per_day=10)
        self._orders(self.yesterday, 4)

        first = sorted(f.headline for f in self._generate().report.findings.all())
        second = sorted(f.headline for f in self._generate().report.findings.all())

        self.assertEqual(first, second)


class ReportIdentityTests(ReportTestCase):
    def test_regenerating_a_day_replaces_its_report(self):
        # A merchant comparing two reports for the same Tuesday would have no way
        # to tell which was true.
        self._baseline(per_day=10)
        self._orders(self.yesterday, 4)

        self._generate()
        self._generate()

        self.assertEqual(Report.objects.filter(covers=self.yesterday).count(), 1)

    def test_regenerating_replaces_the_findings_rather_than_adding_to_them(self):
        self._baseline(per_day=10)
        self._orders(self.yesterday, 4)

        first = self._generate()
        count = first.report.findings.count()
        second = self._generate()

        self.assertEqual(second.report.findings.count(), count)

    def test_the_worker_entrypoint_reports_on_yesterday(self):
        # Yesterday rather than today: a report for a day that is still
        # happening changes under whoever is reading it.
        self._baseline(per_day=10)
        self._orders(self.yesterday, 4)

        rows = services.generate_daily()

        self.assertEqual(rows[0]["covers"], self.yesterday.isoformat())


class LocalNarrationTests(ReportTestCase):
    def test_the_default_provider_narrates_without_spending_anything(self):
        self._baseline(per_day=10)
        self._orders(self.yesterday, 4)

        result = self._generate()

        self.assertEqual(providers.current().name, providers.LOCAL)
        self.assertTrue(result.narrated)
        self.assertEqual(result.report.tokens_used, 0)
        self.assertEqual(result.report.cost, Decimal("0"))
        self.assertEqual(result.report.state, RunState.NARRATED)

    def test_an_urgent_finding_changes_the_opening_line(self):
        self._baseline(per_day=10)
        self._orders(self.yesterday, 4)

        self.assertIn("needs attention", self._generate().report.narrative)

    def test_a_steady_period_reads_as_ordinary(self):
        self._baseline(per_day=10)
        self._orders(self.yesterday, 10)

        self.assertIn("looks ordinary", self._generate().report.narrative)

    def test_the_most_urgent_finding_comes_first(self):
        self._baseline(per_day=10, value=100)
        self._orders(self.yesterday, 4, value=40)

        lines = [line for line in self._generate().report.narrative.splitlines() if line.startswith("- ")]
        first = lines[0]

        self.assertTrue(
            "down" in first,
            f"expected the urgent finding first, got {first!r}",
        )


class BudgetTests(ReportTestCase):
    def setUp(self):
        super().setUp()
        self._baseline(per_day=10)
        self._orders(self.yesterday, 4)

    def test_the_local_provider_is_never_charged_against_the_budget(self):
        self._generate()

        self.assertEqual(services.usage()["tokensUsed"], 0)

    def test_narration_is_refused_when_the_budget_is_spent(self):
        record = Budget.current()
        record.tokens_used = record.monthly_token_cap
        record.save()

        with patch("knight_feature_ai_reports.config._document", return_value=API_CONFIG):
            result = self._generate()

        self.assertFalse(result.narrated)
        self.assertEqual(result.report.state, RunState.REFUSED)
        self.assertIn("budget is spent", result.report.narration_note)

    def test_the_findings_survive_a_refusal(self):
        # The whole reason the split exists. A store over budget still gets the
        # part it can act on.
        record = Budget.current()
        record.tokens_used = record.monthly_token_cap
        record.save()

        with patch("knight_feature_ai_reports.config._document", return_value=API_CONFIG):
            result = self._generate()

        self.assertGreater(result.findings, 0)
        self.assertTrue(result.report.findings.exists())

    def test_an_over_budget_store_never_calls_the_provider(self):
        # Refusing after the fact would be a limit that costs money to enforce.
        record = Budget.current()
        record.tokens_used = record.monthly_token_cap
        record.save()

        with patch("knight_feature_ai_reports.config._document", return_value=API_CONFIG):
            with patch.object(providers.ApiProvider, "narrate") as narrate:
                self._generate()

        narrate.assert_not_called()

    def test_a_call_is_priced_before_it_is_made(self):
        # A budget that can only be checked after the money is spent is not a
        # budget.
        tokens = providers.estimate_tokens([object()] * 3)

        self.assertGreater(tokens, 0)
        self.assertGreater(providers.price(tokens), Decimal("0"))

    def test_the_window_rolls_over_on_read_rather_than_by_a_job(self):
        # A counter that depends on a job having run is a counter that is wrong
        # after an outage, and this one decides whether money is spent.
        record = Budget.current()
        record.tokens_used = 5000
        record.window_started_on = date(2020, 1, 1)
        record.save()

        self.assertEqual(services.budget().tokens_used, 0)

    def test_the_caps_follow_the_configuration(self):
        # Raising a customer's limit is a configuration change delivered over the
        # install channel, not a database edit.
        with patch(
            "knight_feature_ai_reports.config._document",
            return_value={"version": 3, "values": {"monthly_token_cap": 999}, "secrets": {}},
        ):
            self.assertEqual(services.budget().monthly_token_cap, 999)

    def test_the_usage_window_is_a_date_and_not_a_timestamp(self):
        self.assertEqual(len(services.usage()["windowStartedOn"]), len("2026-08-26"))


class RedactionTests(ReportTestCase):
    """
    What may leave the store. An allow-list, because a deny-list is one new
    field away from leaking (adr/0030).
    """

    def _findings(self):
        self._baseline(per_day=10)
        analytics.record(
            "order.placed", {"value": 100000}, occurred_at=self._at(self.yesterday, 10), subject="whale"
        )
        analytics.record(
            "order.placed", {"value": 100}, occurred_at=self._at(self.yesterday, 11), subject="minnow"
        )

        return analysis.compute(analytics, covers=self.yesterday, period=Period.DAY)

    def test_the_payload_carries_only_allow_listed_evidence(self):
        allowed = {"current", "baseline", "change", "orders", "revenue", "customers", "largestShare"}

        for entry in providers.redact(self._findings()):
            self.assertTrue(set(entry["evidence"]) <= allowed, entry)

    def test_an_unexpected_evidence_key_is_dropped(self):
        finding = analysis.Finding(
            code="test",
            headline="Something happened.",
            evidence={"orders": 4, "customer_email": "private@example.test"},
        )

        entry = providers.redact([finding])[0]

        self.assertIn("orders", entry["evidence"])
        self.assertNotIn("customer_email", entry["evidence"])

    def test_no_customer_reference_appears_anywhere_in_the_payload(self):
        payload = str(providers.redact(self._findings()))

        self.assertNotIn("whale", payload)
        self.assertNotIn("minnow", payload)

    def test_the_payload_is_only_codes_severities_headlines_and_evidence(self):
        for entry in providers.redact(self._findings()):
            self.assertEqual(set(entry), {"code", "severity", "headline", "evidence"})


class ProviderTests(ReportTestCase):
    def test_the_manifest_names_the_secret_and_values_nothing(self):
        self.assertEqual(config.SECRET_API_KEY, "model_api_key")
        self.assertEqual(config.secret(config.SECRET_API_KEY), "")

    def test_the_api_provider_refuses_when_the_secret_never_arrived(self):
        with patch(
            "knight_feature_ai_reports.config._document",
            return_value={"version": 1, "values": {"provider": "api"}, "secrets": {}},
        ):
            narration = providers.current().narrate([], period="Day", covers=self.yesterday)

        self.assertTrue(narration.refused)
        self.assertIn("model_api_key", narration.detail)

    def test_the_api_provider_never_leaks_the_key(self):
        with patch("knight_feature_ai_reports.config._document", return_value=API_CONFIG):
            narration = providers.current().narrate([], period="Day", covers=self.yesterday)

        self.assertNotIn("a-real-looking-key", narration.detail)

    def test_describe_reports_secret_names_and_never_values(self):
        with patch("knight_feature_ai_reports.config._document", return_value=API_CONFIG):
            described = config.describe()

        self.assertEqual(described["secretsPresent"], ["model_api_key"])
        self.assertNotIn("a-real-looking-key", str(described))

    def test_an_unknown_provider_falls_back_to_sending_nothing(self):
        # A typo in a configuration value must not be what sends a store's
        # figures to a third party.
        with patch(
            "knight_feature_ai_reports.config._document",
            return_value={"version": 1, "values": {"provider": "some-vendor"}, "secrets": {}},
        ):
            self.assertEqual(providers.current().name, providers.LOCAL)

    def test_an_unreadable_configuration_falls_back_to_the_local_provider(self):
        # A configuration this Feature cannot read must never fail open onto a
        # paid provider.
        with patch("knight_feature_ai_reports.config._from_file", return_value={}):
            with patch("knight_feature_ai_reports.config._from_settings", return_value={}):
                self.assertEqual(providers.current().name, providers.LOCAL)


class DependencyBoundaryTests(ReportTestCase):
    def test_a_missing_analytics_raises_rather_than_reporting_a_quiet_week(self):
        with patch(
            "knight_feature_ai_reports.services._analytics",
            side_effect=services.DependencyUnavailable("not installed"),
        ):
            with self.assertRaises(services.DependencyUnavailable):
                self._generate()

    def test_an_analytics_too_old_to_group_events_is_refused_by_name(self):
        class OldAnalytics:
            """1.0.x: records events, cannot group them."""

        with patch("knight_feature_analytics_core.services", OldAnalytics()):
            with self.assertRaises(services.DependencyUnavailable) as caught:
                services._analytics()

        self.assertIn("1.1.0", str(caught.exception))

    def test_it_reads_the_service_surface_and_not_the_models(self):
        with open(services.__file__, encoding="utf-8") as handle:
            text = handle.read()

        self.assertNotIn("knight_feature_analytics_core.models", text)


class DeliveredRoutesTests(ReportTestCase):
    def test_the_latest_report_is_mounted_under_the_declared_prefix(self):
        self._baseline(per_day=10)
        self._orders(self.yesterday, 4)
        self._generate()

        payload = self.client.get("/ai-reports/").json()

        self.assertEqual(payload["covers"], self.yesterday.isoformat())
        self.assertTrue(payload["findings"])

    def test_a_store_with_no_report_says_so(self):
        self.assertEqual(self.client.get("/ai-reports/").status_code, 404)

    def test_the_history_is_readable(self):
        self._baseline(per_day=10)
        self._orders(self.yesterday, 4)
        self._generate()

        self.assertEqual(len(self.client.get("/ai-reports/history/").json()["reports"]), 1)

    def test_usage_is_readable_so_a_merchant_can_see_their_spend(self):
        payload = self.client.get("/ai-reports/usage/").json()

        self.assertIn("tokenCap", payload)
        self.assertIn("costRemaining", payload)

    def test_the_configuration_endpoint_never_returns_a_secret_value(self):
        with patch("knight_feature_ai_reports.config._document", return_value=API_CONFIG):
            body = self.client.get("/ai-reports/configuration/").content.decode()

        self.assertIn("model_api_key", body)
        self.assertNotIn("a-real-looking-key", body)


class HealthCheckTests(ReportTestCase):
    def test_it_passes_on_a_working_install(self):
        from knight_feature_ai_reports.checks import health

        self.assertTrue(health())

    def test_it_fails_when_the_cap_is_not_a_usable_limit(self):
        # An AI Feature whose install left it with no cap would be a bill nobody
        # agreed to.
        from knight_feature_ai_reports.checks import health

        record = Budget.current()
        record.monthly_token_cap = 0
        record.save()

        with patch(
            "knight_feature_ai_reports.config._document",
            return_value={"version": 1, "values": {"monthly_token_cap": 0}, "secrets": {}},
        ):
            self.assertFalse(health())

    def test_it_fails_when_analytics_is_unusable(self):
        from knight_feature_ai_reports.checks import health

        with patch(
            "knight_feature_ai_reports.services._analytics",
            side_effect=services.DependencyUnavailable("too old"),
        ):
            self.assertFalse(health())
