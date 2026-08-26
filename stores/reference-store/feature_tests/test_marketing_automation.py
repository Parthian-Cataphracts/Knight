"""
`marketing-automation`, installed.

The first Feature that sends mail to real people on a timer, so most of what is
pinned here is the *not sending*: nobody twice, nobody without recorded consent,
nobody on the suppression list, and nothing at all until a merchant switches a
campaign on.

The secret handling has tests of its own. A named-not-valued secret is only
worth anything if the value cannot be read back out through a describe endpoint
or leaked into an error message, so both are asserted.
"""

from datetime import timedelta
from unittest import skipUnless
from unittest.mock import patch

from django.test import TestCase
from django.utils import timezone

from feature_tests.support import installed, require

APP = "knight_feature_marketing_automation"
SEGMENTATION = "knight_feature_customer_segmentation"
ANALYTICS = "knight_feature_analytics_core"
INSTALLED = installed(APP) and installed(SEGMENTATION) and installed(ANALYTICS)
require(APP, SEGMENTATION, ANALYTICS)

if INSTALLED:  # pragma: no cover - guarded above
    from knight_feature_analytics_core import services as analytics
    from knight_feature_customer_segmentation import services as segmentation
    from knight_feature_marketing_automation import config, providers, services
    from knight_feature_marketing_automation.models import (
        Campaign,
        Contact,
        Send,
        SendState,
        Suppression,
        SuppressionReason,
        Trigger,
    )

#: Events are recorded this far in the past so they fall inside a trigger window
#: rather than exactly on its boundary. Same reasoning as the segmentation tests.
SETTLED = timedelta(hours=2)


@skipUnless(INSTALLED, "marketing-automation and its dependencies are not installed.")
class MarketingTestCase(TestCase):
    def setUp(self):
        services.ensure_default_campaigns()
        segmentation.ensure_defaults()
        self.now = timezone.now()

    def _new_customers(self, *subjects: str) -> None:
        """Puts subjects into the new-customers segment, via a real order event."""
        for subject in subjects:
            analytics.record(
                "order.placed",
                {"value": 50000},
                occurred_at=self.now - SETTLED,
                subject=subject,
            )

        segmentation.recalculate(at=self.now)

    def _welcome(self, **overrides) -> "Campaign":
        campaign = Campaign.objects.get(slug="welcome")
        campaign.is_active = True
        campaign.delay_hours = 0

        for field, value in overrides.items():
            setattr(campaign, field, value)

        campaign.save()

        return campaign


class SafeDefaultsTests(MarketingTestCase):
    def test_every_seeded_campaign_is_switched_off(self):
        # A marketing package that began mailing the moment it was installed
        # would be the worst default in this catalogue.
        self.assertEqual(Campaign.objects.count(), 4)
        self.assertFalse(Campaign.objects.filter(is_active=True).exists())

    def test_the_default_provider_sends_nothing(self):
        self.assertEqual(providers.current().name, providers.RECORDING)

    def test_an_inactive_campaign_does_nothing_even_with_an_audience(self):
        self._new_customers("ann")
        services.register_contact("ann", "ann@example.test")

        report = services.run(Campaign.objects.get(slug="welcome"), at=self.now)

        self.assertTrue(report.did_nothing)
        self.assertEqual(Send.objects.count(), 0)

    def test_seeding_twice_creates_nothing(self):
        self.assertEqual(services.ensure_default_campaigns(), 0)

    def test_a_campaign_cannot_be_paused_by_setting_its_cap_to_zero(self):
        # Zero would be a campaign that is active and sends nothing, which reads
        # as broken. is_active is how you pause.
        campaign = Campaign.objects.get(slug="welcome")
        campaign.maximum_per_run = 0

        with self.assertRaises(Exception):
            campaign.full_clean()


class ConsentTests(MarketingTestCase):
    def test_somebody_with_no_recorded_contact_is_not_mailed(self):
        # Consent is a fact the store collected. Inferring it from the existence
        # of a customer would be inventing permission nobody gave.
        self._new_customers("cara")
        campaign = self._welcome()

        report = services.run(campaign, at=self.now)

        self.assertEqual(report.no_contact, 1)
        self.assertEqual(report.sent, 0)
        self.assertEqual(Send.objects.get(subject_ref="cara").state, SendState.NO_CONTACT)

    def test_registering_a_contact_records_when_consent_was_given(self):
        contact = services.register_contact("ann", "ann@example.test")

        self.assertIsNotNone(contact.consented_at)

    def test_an_address_is_stored_lowercased_so_suppression_matches_it(self):
        services.register_contact("ann", "  ANN@Example.TEST ")

        self.assertEqual(Contact.objects.get(subject="ann").email, "ann@example.test")

    def test_a_contact_with_no_address_is_refused(self):
        with self.assertRaises(ValueError):
            services.register_contact("ann", "   ")


class SuppressionTests(MarketingTestCase):
    def test_a_suppressed_address_is_never_mailed(self):
        self._new_customers("ben")
        services.register_contact("ben", "ben@example.test")
        services.suppress("ben@example.test")
        campaign = self._welcome()

        report = services.run(campaign, at=self.now)

        self.assertEqual(report.suppressed, 1)
        self.assertEqual(report.sent, 0)

    def test_suppression_is_keyed_on_the_address_not_the_customer(self):
        # Somebody who unsubscribes has withdrawn permission for that address. A
        # store registering it under a new customer id must not get a fresh start.
        services.suppress("shared@example.test")
        self._new_customers("first", "second")
        services.register_contact("first", "shared@example.test")
        services.register_contact("second", "shared@example.test")
        campaign = self._welcome()

        report = services.run(campaign, at=self.now)

        self.assertEqual(report.suppressed, 2)
        self.assertEqual(report.sent, 0)

    def test_suppressing_the_same_address_twice_keeps_one_row(self):
        services.suppress("ben@example.test", reason=SuppressionReason.BOUNCED)
        services.suppress("ben@example.test", reason=SuppressionReason.COMPLAINED)

        self.assertEqual(Suppression.objects.filter(email="ben@example.test").count(), 1)

    def test_suppression_is_matched_case_insensitively(self):
        services.suppress("BEN@Example.TEST")

        self.assertTrue(services.is_suppressed("ben@example.test"))


class SendOnceTests(MarketingTestCase):
    def test_a_campaign_mails_somebody_at_most_once(self):
        # The whole safety property, and the constraint is what guarantees it
        # rather than a check a second run would also pass.
        self._new_customers("ann")
        services.register_contact("ann", "ann@example.test")
        campaign = self._welcome()

        first = services.run(campaign, at=self.now)
        second = services.run(campaign, at=self.now)

        self.assertEqual(first.sent, 1)
        self.assertEqual(second.sent, 0)
        self.assertEqual(second.already_sent, 1)
        self.assertEqual(Send.objects.filter(subject_ref="ann").count(), 1)

    def test_the_body_is_stored_as_it_was_sent(self):
        # A template changes; what somebody was actually told does not, and a
        # complaint about a message from March needs the March wording.
        self._new_customers("ann")
        services.register_contact("ann", "ann@example.test")
        campaign = self._welcome(body="Original wording for {{ subject }}")

        services.run(campaign, at=self.now)

        campaign.body = "Rewritten later"
        campaign.save()

        self.assertIn("Original wording for ann", Send.objects.get(subject_ref="ann").body)

    def test_the_provider_message_id_is_recorded_for_tracing(self):
        self._new_customers("ann")
        services.register_contact("ann", "ann@example.test")

        services.run(self._welcome(), at=self.now)

        self.assertTrue(Send.objects.get(subject_ref="ann").provider_message_id)

    def test_a_cap_bounds_one_run(self):
        # The first thing that goes wrong with automated mail is volume.
        self._new_customers("a", "b", "c", "d", "e")

        for subject in ("a", "b", "c", "d", "e"):
            services.register_contact(subject, f"{subject}@example.test")

        report = services.run(self._welcome(maximum_per_run=2), at=self.now)

        self.assertEqual(report.considered, 2)
        self.assertEqual(report.sent, 2)


class DryRunTests(MarketingTestCase):
    def test_a_dry_run_reports_accurately_and_writes_nothing(self):
        self._new_customers("ann", "ben", "cara")
        services.register_contact("ann", "ann@example.test")
        services.register_contact("ben", "ben@example.test")
        services.suppress("ben@example.test")
        campaign = self._welcome()

        report = services.run(campaign, at=self.now, dry_run=True)

        self.assertEqual(report.considered, 3)
        self.assertEqual(report.sent, 1)
        self.assertEqual(report.suppressed, 1)
        self.assertEqual(report.no_contact, 1)
        self.assertEqual(Send.objects.count(), 0)

    def test_a_dry_run_does_not_mark_the_campaign_as_run(self):
        # Otherwise the next real run would think it had already happened.
        campaign = self._welcome()
        services.run(campaign, at=self.now, dry_run=True)

        campaign.refresh_from_db()
        self.assertIsNone(campaign.last_run_at)


class AudienceTests(MarketingTestCase):
    def test_welcome_draws_from_the_new_customers_segment(self):
        self._new_customers("ann")

        self.assertEqual(services.audience_for(self._welcome(), at=self.now), ["ann"])

    def test_a_segment_narrows_the_audience_rather_than_replacing_it(self):
        # A merchant who set both meant the intersection, which is the only
        # reading that makes the second field useful.
        self._new_customers("ann")
        campaign = self._welcome(segment_slug="dormant-customers")

        self.assertEqual(services.audience_for(campaign, at=self.now), [])

    def test_post_purchase_waits_for_the_delay_before_considering_anybody(self):
        analytics.record(
            "order.placed", {"value": 1000}, occurred_at=self.now - timedelta(hours=1), subject="ann"
        )

        campaign = Campaign.objects.get(slug="post-purchase")
        campaign.is_active = True
        campaign.delay_hours = 72
        campaign.save()

        # One hour old against a 72-hour delay: too soon.
        self.assertEqual(services.audience_for(campaign, at=self.now), [])

        # And in range once three days have passed.
        later = self.now + timedelta(hours=72)
        self.assertEqual(services.audience_for(campaign, at=later), ["ann"])

    def test_an_unknown_trigger_considers_nobody_rather_than_everybody(self):
        campaign = self._welcome(trigger="NotATrigger")

        self.assertEqual(services.audience_for(campaign, at=self.now), [])

    def test_the_audience_is_deduplicated(self):
        for _ in range(3):
            analytics.record(
                "order.placed", {"value": 1000}, occurred_at=self.now - SETTLED, subject="ann"
            )

        segmentation.recalculate(at=self.now)

        self.assertEqual(services.audience_for(self._welcome(), at=self.now), ["ann"])


class DependencyBoundaryTests(MarketingTestCase):
    def test_a_missing_segmentation_raises_rather_than_mailing_nobody(self):
        # A run that quietly mailed nobody because segmentation was absent looks
        # exactly like a store with no customers.
        with patch(
            "knight_feature_marketing_automation.services._segmentation",
            side_effect=services.DependencyUnavailable("not installed"),
        ):
            with self.assertRaises(services.DependencyUnavailable):
                services.audience_for(self._welcome(), at=self.now)

    def test_an_analytics_too_old_to_group_events_is_refused_by_name(self):
        class OldAnalytics:
            """1.0.x: records events, cannot group them."""

        with patch("knight_feature_analytics_core.services", OldAnalytics()):
            with self.assertRaises(services.DependencyUnavailable) as caught:
                services._analytics()

        self.assertIn("1.1.0", str(caught.exception))

    def test_it_reads_service_surfaces_and_not_models(self):
        # The property that makes the declared version ranges honest.
        source = services.__file__

        with open(source, encoding="utf-8") as handle:
            text = handle.read()

        self.assertNotIn("knight_feature_customer_segmentation.models", text)
        self.assertNotIn("knight_feature_analytics_core.models", text)

    def test_one_failing_campaign_does_not_stop_the_others(self):
        # A broken template on a win-back must not cost the store its
        # post-purchase mail.
        self._new_customers("ann")
        services.register_contact("ann", "ann@example.test")
        self._welcome()

        broken = Campaign.objects.get(slug="win-back")
        broken.is_active = True
        broken.trigger = "NotATrigger"
        broken.save()

        reports = services.run_all(at=self.now)

        self.assertEqual(sum(report.sent for report in reports), 1)


class SecretTests(MarketingTestCase):
    """
    Named, never valued. A secret is only worth handling carefully if the value
    cannot be read back out, so that is what these check.
    """

    CONFIGURED = {
        "version": 3,
        "values": {"provider": "api", "from_email": "shop@example.test"},
        "secrets": {"email_api_key": "a-real-looking-key-0123456789"},
    }

    def test_the_manifest_names_the_secret_and_values_nothing(self):
        self.assertEqual(config.SECRET_API_KEY, "email_api_key")
        self.assertEqual(config.secret(config.SECRET_API_KEY), "")

    def test_a_delivered_secret_is_read(self):
        with patch(
            "knight_feature_marketing_automation.config._document", return_value=self.CONFIGURED
        ):
            self.assertTrue(config.secret("email_api_key"))

    def test_describe_reports_the_name_and_never_the_value(self):
        with patch(
            "knight_feature_marketing_automation.config._document", return_value=self.CONFIGURED
        ):
            described = config.describe()

        self.assertEqual(described["secretsPresent"], ["email_api_key"])
        self.assertNotIn("a-real-looking-key", str(described))

    def test_the_configuration_endpoint_never_returns_a_secret_value(self):
        with patch(
            "knight_feature_marketing_automation.config._document", return_value=self.CONFIGURED
        ):
            body = self.client.get("/marketing/configuration/").content.decode()

        self.assertIn("email_api_key", body)
        self.assertNotIn("a-real-looking-key", body)

    def test_the_api_provider_refuses_without_leaking_the_key(self):
        with patch(
            "knight_feature_marketing_automation.config._document", return_value=self.CONFIGURED
        ):
            delivery = providers.current().send(
                to="x@example.test", subject="s", body="b", from_email="shop@example.test"
            )

        self.assertFalse(delivery.delivered)
        self.assertNotIn("a-real-looking-key", delivery.detail)

    def test_the_api_provider_says_so_when_the_secret_never_arrived(self):
        with patch(
            "knight_feature_marketing_automation.config._document",
            return_value={"version": 1, "values": {"provider": "api"}, "secrets": {}},
        ):
            delivery = providers.current().send(
                to="x@example.test", subject="s", body="b", from_email=""
            )

        self.assertFalse(delivery.delivered)
        self.assertIn("email_api_key", delivery.detail)

    def test_an_unknown_provider_falls_back_to_sending_nothing(self):
        # A typo in a configuration value must not mail a customer list.
        with patch(
            "knight_feature_marketing_automation.config._document",
            return_value={"version": 1, "values": {"provider": "sendmail-please"}, "secrets": {}},
        ):
            self.assertEqual(providers.current().name, providers.RECORDING)


class DeliveredRoutesTests(MarketingTestCase):
    def test_the_overview_is_mounted_under_the_declared_prefix(self):
        payload = self.client.get("/marketing/").json()

        self.assertEqual(len(payload["campaigns"]), 4)

    def test_a_campaign_history_is_readable(self):
        self._new_customers("ann")
        services.register_contact("ann", "ann@example.test")
        services.run(self._welcome(), at=self.now)

        payload = self.client.get("/marketing/welcome/history/").json()

        self.assertEqual(payload["sends"][0]["subject"], "ann")

    def test_unsubscribing_suppresses_the_address(self):
        response = self.client.post(
            "/marketing/unsubscribe/",
            data={"email": "ben@example.test"},
            content_type="application/json",
        )

        self.assertEqual(response.status_code, 200)
        self.assertTrue(services.is_suppressed("ben@example.test"))

    def test_unsubscribing_an_unknown_address_answers_the_same_way(self):
        # Confirming which addresses a shop holds would turn this into a way of
        # checking whether somebody is a customer.
        known = self.client.post(
            "/marketing/unsubscribe/",
            data={"email": "ben@example.test"},
            content_type="application/json",
        )
        unknown = self.client.post(
            "/marketing/unsubscribe/",
            data={"email": "nobody@example.test"},
            content_type="application/json",
        )

        self.assertEqual(known.json(), unknown.json())

    def test_unsubscribing_takes_post_only(self):
        self.assertEqual(self.client.get("/marketing/unsubscribe/").status_code, 405)

    def test_unsubscribing_without_an_address_is_refused(self):
        response = self.client.post(
            "/marketing/unsubscribe/", data={}, content_type="application/json"
        )

        self.assertEqual(response.status_code, 400)


class HealthCheckTests(MarketingTestCase):
    def test_it_passes_on_a_working_install(self):
        from knight_feature_marketing_automation.checks import health

        self.assertTrue(health())

    def test_it_fails_when_the_segmentation_dependency_is_unusable(self):
        from knight_feature_marketing_automation.checks import health

        with patch(
            "knight_feature_marketing_automation.services._segmentation",
            side_effect=services.DependencyUnavailable("too old"),
        ):
            self.assertFalse(health())
