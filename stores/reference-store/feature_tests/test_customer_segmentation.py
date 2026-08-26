"""
`customer-segmentation`, installed.

The Feature whose data comes from another Feature. Most of what matters here is
the boundary: it reads `analytics-core` through its service surface and never its
models, it fails loudly rather than quietly when that dependency is missing or
too old, and a recomputation replaces memberships rather than accumulating them.
"""

from datetime import datetime, timedelta, timezone
from decimal import Decimal
from unittest import skipUnless
from unittest.mock import patch

from django.test import TestCase

from feature_tests.support import installed, require

APP = "knight_feature_customer_segmentation"
ANALYTICS = "knight_feature_analytics_core"
INSTALLED = installed(APP) and installed(ANALYTICS)
require(APP, ANALYTICS)

if INSTALLED:  # pragma: no cover - guarded above
    from knight_feature_analytics_core import services as analytics
    from knight_feature_customer_segmentation import services
    from knight_feature_customer_segmentation.models import (
        Segment,
        SegmentMembership,
        SegmentRule,
        SegmentStatus,
    )


def _now() -> datetime:
    return datetime.now(timezone.utc)


#: Events are recorded at least this far before the moment a run is asked about.
#: `subjects_between` scans a half-open window - [start, end) - so an event
#: stamped at exactly the run instant is outside it. That is the correct shape
#: for a window and irrelevant in production, where an order is always
#: fractionally in the past; it matters only to fixtures that would otherwise sit
#: exactly on the boundary.
SETTLED = timedelta(hours=1)


@skipUnless(INSTALLED, "customer-segmentation or analytics-core is not installed.")
class DefaultSegmentTests(TestCase):
    def test_the_five_definitions_a_merchant_expects_are_seeded(self):
        # A segmentation feature that installs empty makes the buyer invent five
        # definitions themselves, which is what they paid to avoid.
        self.assertEqual(services.ensure_defaults(), 5)
        self.assertEqual(
            sorted(Segment.objects.values_list("slug", flat=True)),
            [
                "dormant-customers",
                "frequent-customers",
                "high-value-customers",
                "new-customers",
                "vip-customers",
            ],
        )

    def test_seeding_twice_creates_nothing_the_second_time(self):
        services.ensure_defaults()

        self.assertEqual(services.ensure_defaults(), 0)
        self.assertEqual(Segment.objects.count(), 5)


@skipUnless(INSTALLED, "customer-segmentation or analytics-core is not installed.")
class RuleTests(TestCase):
    """
    One customer per rule, so a rule that matched everybody would be obvious.
    """

    def setUp(self):
        services.ensure_defaults()
        self.now = _now()

        profile = [
            # subject, orders, value each, days since most recent
            ("vip-vera", 8, 400000, 0),
            ("regular-reza", 4, 120000, 1),
            ("bigspend-bita", 1, 3000000, 2),
            ("newcomer-nima", 1, 50000, 0),
            ("lapsed-laleh", 3, 200000, 120),
        ]

        for subject, count, value, days_ago in profile:
            for offset in range(count):
                analytics.record(
                    "order.placed",
                    {"value": value},
                    occurred_at=self.now - timedelta(days=days_ago + offset) - SETTLED,
                    subject=subject,
                )

        services.recalculate(at=self.now)

    def _members(self, slug: str) -> set[str]:
        return {row["subject"] for row in services.members_of(slug)}

    def test_vip_needs_both_frequency_and_value(self):
        # Somebody who ordered once for a large amount is high-value; a VIP is
        # somebody who keeps coming back and spends.
        self.assertEqual(self._members("vip-customers"), {"vip-vera"})

    def test_high_value_counts_one_large_order(self):
        self.assertEqual(
            self._members("high-value-customers"), {"vip-vera", "bigspend-bita"}
        )

    def test_frequent_counts_orders_not_money(self):
        self.assertEqual(self._members("frequent-customers"), {"vip-vera", "regular-reza"})

    def test_dormant_is_measured_from_the_last_order(self):
        self.assertEqual(self._members("dormant-customers"), {"lapsed-laleh"})

    def test_a_customer_can_be_in_several_segments(self):
        self.assertEqual(
            sorted(services.segments_for("vip-vera")),
            ["frequent-customers", "high-value-customers", "new-customers", "vip-customers"],
        )

    def test_membership_records_why_somebody_is_in_the_list(self):
        # A merchant looking at a VIP list wants to know why each name is on it.
        row = services.members_of("vip-customers")[0]

        self.assertEqual(row["events"], 8)
        self.assertEqual(Decimal(row["totalValue"]), Decimal("3200000.00"))
        self.assertIsNotNone(row["lastSeenAt"])


@skipUnless(INSTALLED, "customer-segmentation or analytics-core is not installed.")
class RecomputationTests(TestCase):
    def setUp(self):
        services.ensure_defaults()
        self.now = _now()

    def _order(self, subject: str, value: int = 100000, days_ago: int = 0):
        analytics.record(
            "order.placed",
            {"value": value},
            occurred_at=self.now - timedelta(days=days_ago) - SETTLED,
            subject=subject,
        )

    def test_somebody_who_no_longer_qualifies_leaves_the_segment(self):
        # An upsert-only run would never remove anybody, and a merchant would
        # keep mailing customers who stopped matching months ago.
        for _ in range(4):
            self._order("reza")

        services.recalculate(at=self.now)
        self.assertIn("reza", {row["subject"] for row in services.members_of("frequent-customers")})

        # Recomputed over a window that excludes those orders entirely.
        services.recalculate(at=self.now + timedelta(days=365))

        self.assertEqual(services.members_of("frequent-customers"), [])

    def test_a_run_reports_what_it_did(self):
        self._order("reza")
        report = services.recalculate(at=self.now)

        self.assertEqual(report.segments, 5)
        self.assertFalse(report.did_nothing)

    def test_a_paused_segment_keeps_what_it_found_rather_than_being_emptied(self):
        # Pausing means "stop maintaining this", not "delete what it found".
        for _ in range(4):
            self._order("reza")

        services.recalculate(at=self.now)
        before = services.members_of("frequent-customers")

        segment = Segment.objects.get(slug="frequent-customers")
        segment.status = SegmentStatus.PAUSED
        segment.save()

        services.recalculate(at=self.now + timedelta(days=365))

        self.assertEqual(services.members_of("frequent-customers"), before)

    def test_events_with_no_subject_are_not_a_customer(self):
        # Every event recorded before analytics-core 1.1.0 has no subject, and a
        # row for the empty string is not somebody a merchant can mail.
        analytics.record("order.placed", {"value": 500000}, occurred_at=self.now - SETTLED)

        services.recalculate(at=self.now)

        self.assertEqual(SegmentMembership.objects.filter(subject="").count(), 0)

    def test_the_run_records_when_each_segment_was_computed(self):
        services.recalculate(at=self.now)

        for row in services.summary():
            self.assertIsNotNone(row["lastComputedAt"], row["slug"])


@skipUnless(INSTALLED, "customer-segmentation or analytics-core is not installed.")
class DependencyBoundaryTests(TestCase):
    """
    The dependency is a real one, and the failure modes are told apart.

    A segmentation run that quietly produced no members because analytics was
    missing would look exactly like a store with no customers — and a merchant
    would act on that.
    """

    def test_a_missing_dependency_raises_rather_than_returning_nothing(self):
        with patch(
            "knight_feature_customer_segmentation.services._analytics",
            side_effect=services.AnalyticsUnavailable("not installed"),
        ):
            with self.assertRaises(services.AnalyticsUnavailable):
                services.recalculate()

    def test_an_analytics_too_old_to_help_is_refused_by_name(self):
        # analytics-core 1.0.x has no per-subject aggregation at all. The
        # manifest declares >=1.1.0 for exactly this reason, and the check has
        # to say so rather than failing with an AttributeError.
        class OldAnalytics:
            """1.0.x: records events, cannot group them."""

            def record(self, *args, **kwargs):
                raise AssertionError("not what is being tested")

        # Patched on the package rather than in sys.modules, because
        # `from pkg import services` reads the attribute off the already-imported
        # package and never consults sys.modules for it.
        with patch("knight_feature_analytics_core.services", OldAnalytics()):
            with self.assertRaises(services.AnalyticsUnavailable) as caught:
                services._analytics()

        self.assertIn("1.1.0", str(caught.exception))

    def test_the_health_check_fails_when_the_dependency_is_unusable(self):
        from knight_feature_customer_segmentation.checks import health

        self.assertTrue(health())

        with patch(
            "knight_feature_customer_segmentation.services._analytics",
            side_effect=services.AnalyticsUnavailable("too old"),
        ):
            self.assertFalse(health())

    def test_segmentation_reads_the_service_surface_not_the_models(self):
        # The property that makes the declared version range honest: analytics
        # may change how it stores events without breaking this.
        source = (
            __import__("knight_feature_customer_segmentation.services", fromlist=["services"])
            .__file__
        )

        with open(source, encoding="utf-8") as handle:
            text = handle.read()

        self.assertNotIn("knight_feature_analytics_core.models", text)
        self.assertIn("from knight_feature_analytics_core import services", text)


@skipUnless(INSTALLED, "customer-segmentation or analytics-core is not installed.")
class DeliveredRoutesTests(TestCase):
    def setUp(self):
        services.ensure_defaults()
        now = _now()

        for _ in range(6):
            analytics.record(
                "order.placed", {"value": 500000}, occurred_at=now - SETTLED, subject="vera"
            )

        services.recalculate(at=now)

    def test_the_overview_is_mounted_under_the_declared_prefix(self):
        payload = self.client.get("/segments/").json()

        self.assertEqual(len(payload["segments"]), 5)

    def test_members_of_a_segment_are_readable(self):
        payload = self.client.get("/segments/vip-customers/members/").json()

        self.assertEqual([row["subject"] for row in payload["members"]], ["vera"])

    def test_the_segments_one_customer_is_in_are_readable(self):
        # The lookup marketing automation makes per recipient.
        payload = self.client.get("/segments/subject/vera/").json()

        self.assertIn("vip-customers", payload["segments"])

    def test_an_unknown_segment_is_an_empty_list_rather_than_an_error(self):
        payload = self.client.get("/segments/does-not-exist/members/").json()

        self.assertEqual(payload["members"], [])


@skipUnless(INSTALLED, "customer-segmentation or analytics-core is not installed.")
class DefinitionValidationTests(TestCase):
    def test_a_dormancy_longer_than_its_window_is_refused(self):
        # Otherwise the window ends before the threshold begins, the segment can
        # never match anybody, and an always-empty segment reads as broken data
        # rather than as a bad definition.
        segment = Segment(
            name="Impossible",
            slug="impossible",
            rule=SegmentRule.DORMANT,
            window_days=30,
            dormant_after_days=60,
        )

        with self.assertRaises(Exception):
            segment.full_clean()
