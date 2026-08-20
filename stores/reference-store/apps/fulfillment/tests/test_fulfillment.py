"""Parity tests for the ported fulfilment settings."""

from decimal import Decimal

from django.core.exceptions import ValidationError
from django.test import TestCase

from apps.fulfillment.models import FulfillmentSettings


class SettingsTests(TestCase):
    def test_a_store_that_has_never_configured_anything_still_works(self):
        # Otherwise the first checkout on a fresh store fails on a missing row,
        # somewhere deep enough that it reads as a bug rather than a setting.
        settings = FulfillmentSettings.current()

        self.assertTrue(settings.collection_enabled)
        self.assertFalse(settings.delivery_enabled)

    def test_there_is_only_ever_one_row(self):
        first = FulfillmentSettings.current()
        second = FulfillmentSettings.current()

        self.assertEqual(first.pk, second.pk)
        self.assertEqual(FulfillmentSettings.objects.count(), 1)

    def test_a_store_must_offer_at_least_one_way_to_receive_goods(self):
        # A store offering neither cannot sell anything, and the failure should
        # arrive here rather than at a shopper's checkout.
        settings = FulfillmentSettings(collection_enabled=False, delivery_enabled=False)

        with self.assertRaises(ValidationError):
            settings.full_clean()

    def test_delivery_only_is_allowed(self):
        settings = FulfillmentSettings(collection_enabled=False, delivery_enabled=True)

        settings.full_clean()

    def test_a_delivery_minimum_can_be_set(self):
        settings = FulfillmentSettings.current()
        settings.delivery_enabled = True
        settings.delivery_minimum_order = Decimal("150000")
        settings.full_clean()
        settings.save()

        self.assertEqual(FulfillmentSettings.current().delivery_minimum_order, Decimal("150000"))
