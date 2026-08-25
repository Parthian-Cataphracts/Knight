"""
Delivery zones, now that they are the base store's.

Moved out of the store suite's Feature-guarded section along with the code
([`adr/0024`](../../../../../docs/adr/0024-base-store-versus-optional-feature.md)).
One behaviour changed in the move and is tested for deliberately: a zone no
longer quotes on a store that has delivery switched off. Under the Feature the
two switches lived in different tables and nothing joined them, so a store could
have zones and no delivery and quote anyway.
"""

from decimal import Decimal

from django.test import TestCase

from apps.fulfillment import services as delivery
from apps.fulfillment.models import DeliveryZone, FulfillmentSettings


class DeliveryQuotingTests(TestCase):
    def setUp(self):
        settings = FulfillmentSettings.current()
        settings.delivery_enabled = True
        settings.save()

        self.zone = DeliveryZone.objects.create(name="Central", fee=Decimal("30000"))

    def test_a_zone_quotes_its_fee(self):
        quote = delivery.quote(self.zone.pk, Decimal("100000"))

        self.assertTrue(quote.accepted)
        self.assertEqual(quote.fee, Decimal("30000"))

    def test_an_unknown_zone_is_refused_with_a_reason(self):
        # "We do not deliver there" and "your basket is too small" lead to
        # completely different next actions for the shopper.
        quote = delivery.quote(999999, Decimal("100000"))

        self.assertFalse(quote.accepted)
        self.assertIn("not available", quote.reason)

    def test_a_store_that_does_not_deliver_quotes_nothing(self):
        # New in the move. Two switches in two tables meant nothing checked the
        # store-level one, so zones quoted on a collection-only store.
        settings = FulfillmentSettings.current()
        settings.delivery_enabled = False
        settings.save()

        quote = delivery.quote(self.zone.pk, Decimal("100000"))

        self.assertFalse(quote.accepted)
        self.assertIn("does not deliver", quote.reason)

    def test_a_basket_below_the_zone_minimum_is_refused(self):
        self.zone.minimum_order_subtotal = Decimal("200000")
        self.zone.save()

        quote = delivery.quote(self.zone.pk, Decimal("100000"))

        self.assertFalse(quote.accepted)
        self.assertIn("Central", quote.reason)

    def test_a_zone_minimum_overrides_the_store_default(self):
        # A far suburb that only makes sense above a larger basket is what this
        # exists for; two figures combined would be impossible to explain.
        settings = FulfillmentSettings.current()
        settings.delivery_minimum_order = Decimal("500000")
        settings.save()

        self.zone.minimum_order_subtotal = Decimal("50000")
        self.zone.save()

        self.assertTrue(delivery.quote(self.zone.pk, Decimal("100000")).accepted)

    def test_a_zero_store_minimum_means_no_minimum(self):
        # Zero and "no minimum" are the same commercial fact. The Feature said it
        # with null and the base store says it with zero, so the conversion has
        # to happen somewhere and it happens once.
        settings = FulfillmentSettings.current()

        self.assertEqual(settings.delivery_minimum_order, Decimal("0"))
        self.assertIsNone(settings.default_minimum_order)
        self.assertTrue(delivery.quote(self.zone.pk, Decimal("1")).accepted)

    def test_pausing_deliveries_refuses_without_changing_the_zones(self):
        # A kitchen stopping for an hour should not have to reconfigure, and
        # turning it back on must restore exactly what was there.
        settings = FulfillmentSettings.current()
        settings.delivery_accepting_orders = False
        settings.save()

        quote = delivery.quote(self.zone.pk, Decimal("100000"))
        self.assertFalse(quote.accepted)
        self.assertIn("paused", quote.reason)

        settings.delivery_accepting_orders = True
        settings.save()

        self.assertTrue(delivery.quote(self.zone.pk, Decimal("100000")).accepted)

    def test_an_archived_zone_frees_its_name(self):
        # A business reorganising its areas should not have to invent a name it
        # has already used.
        self.zone.archive()
        self.zone.save()

        DeliveryZone.objects.create(name="Central", fee=Decimal("40000"))

        self.assertEqual(DeliveryZone.objects.filter(name="Central").count(), 2)

    def test_the_choices_offered_are_empty_when_the_store_does_not_deliver(self):
        settings = FulfillmentSettings.current()
        settings.delivery_enabled = False
        settings.save()

        self.assertEqual(delivery.zones(), [])
