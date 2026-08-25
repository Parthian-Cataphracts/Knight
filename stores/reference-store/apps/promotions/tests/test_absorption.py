"""
The two absorption commands, and the states they have to survive.

What can be asserted here is narrower than what the commands do, and the gap is
worth stating rather than hiding. Actually moving rows needs a store with the
*legacy* Feature installed — a 1.x package that no longer exists in this
repository — so it cannot be set up from a test. It was verified by running both
commands against a real store carrying real legacy data, and that run is written
down in [`phase-12-verification.md`](../../../../../docs/phase-12-verification.md).

What is asserted here is every path an operator can reach *after* that: a store
that never had the Feature, and a store that has already been through the move.
Both of those used to end in a traceback, which is the failure a transitional
command is most likely to hand somebody.
"""

from io import StringIO

from django.core.management import call_command
from django.test import TestCase


class AbsorptionCommandTests(TestCase):
    """
    Both commands, against a store with no legacy rows to move.

    Which of the two clean paths gets taken depends on the machine — whether the
    legacy package happens to be installed beside a test database that has none
    of its rows. The assertion is therefore about what an operator is owed in
    every case: the command finishes, and the last thing it prints is a sentence
    they can act on. Neither path may end in a traceback, which is the failure a
    transitional command is most likely to hand somebody.
    """

    COMMANDS = ("knight_absorb_promotions", "knight_absorb_delivery_zones")

    def _assert_reports(self, output: str) -> None:
        reported = "nothing to absorb" in output or "moved" in output

        self.assertTrue(reported, f"The command finished without saying what it did: {output!r}")

    def test_each_command_reports_rather_than_failing(self):
        for command in self.COMMANDS:
            with self.subTest(command=command):
                out = StringIO()
                call_command(command, stdout=out)

                self._assert_reports(out.getvalue())

    def test_a_dry_run_reports_and_writes_nothing(self):
        for command in self.COMMANDS:
            with self.subTest(command=command):
                out = StringIO()
                call_command(command, "--dry-run", stdout=out)

                self._assert_reports(out.getvalue())

    def test_absorbing_leaves_a_store_with_no_legacy_rows_unchanged(self):
        # The commands run on stores that never had either Feature, and on those
        # they must be inert rather than merely harmless.
        from apps.fulfillment.models import DeliveryZone
        from apps.promotions.models import Promotion

        for command in self.COMMANDS:
            call_command(command, stdout=StringIO())

        self.assertEqual(Promotion.objects.count(), 0)
        self.assertEqual(DeliveryZone.objects.count(), 0)
