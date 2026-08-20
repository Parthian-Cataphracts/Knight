"""Parity tests for the ported shopper domain."""

from django.core.exceptions import ValidationError
from django.db import IntegrityError, transaction
from django.test import TestCase

from apps.shoppers.models import Shopper, ShopperStatus, normalize_phone


class PhoneNormalizationTests(TestCase):
    """
    The definition of "the same person".

    Uniqueness is declared on the normalised value, so these cases are the
    constraint rather than a convenience beside it.
    """

    def test_the_three_shapes_of_one_iranian_number_match(self):
        national = normalize_phone("09123456789")

        self.assertEqual(normalize_phone("+989123456789"), national)
        self.assertEqual(normalize_phone("00989123456789"), national)

    def test_separators_and_spacing_are_ignored(self):
        self.assertEqual(normalize_phone("0912 345 6789"), normalize_phone("0912-345-6789"))

    def test_something_too_short_is_refused(self):
        with self.assertRaises(ValidationError):
            normalize_phone("12345")

    def test_blank_is_refused(self):
        with self.assertRaises(ValidationError):
            normalize_phone("   ")


class ShopperTests(TestCase):
    def test_the_same_person_typed_two_ways_is_one_shopper(self):
        Shopper.objects.create(display_name="Ali", phone="09123456789")

        with self.assertRaises(IntegrityError), transaction.atomic():
            Shopper.objects.create(display_name="Ali again", phone="+98 912 345 6789")

    def test_the_number_is_kept_as_typed_and_matched_normalised(self):
        # The merchant should see what they entered; matching is a separate
        # concern and should not rewrite their data.
        shopper = Shopper.objects.create(display_name="Ali", phone="+98 912 345 6789")

        self.assertEqual(shopper.phone, "+98 912 345 6789")
        self.assertEqual(shopper.normalized_phone, "09123456789")

    def test_a_blocked_shopper_cannot_order_but_is_not_deleted(self):
        # Their history is the reason they were blocked and the reason a dispute
        # can be settled later.
        shopper = Shopper.objects.create(display_name="Ali", phone="09123456789")
        shopper.block("Repeated chargebacks.")
        shopper.save()

        shopper.refresh_from_db()

        self.assertEqual(shopper.status, ShopperStatus.BLOCKED)
        self.assertFalse(shopper.can_order)
        self.assertIn("chargebacks", shopper.notes)
        self.assertTrue(Shopper.objects.filter(pk=shopper.pk).exists())

    def test_an_active_shopper_can_order(self):
        shopper = Shopper.objects.create(display_name="Ali", phone="09123456789")

        self.assertTrue(shopper.can_order)

    def test_email_is_optional(self):
        # A counter takes a phone number, not an email address.
        shopper = Shopper.objects.create(display_name="Ali", phone="09123456789")

        self.assertEqual(shopper.email, "")
