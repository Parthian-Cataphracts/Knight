"""
Moves delivery zones out of the old delivery Feature and into the base store.

The companion to `knight_absorb_promotions`, and it exists for the same reason:
phase 12 moved shipping into the base image
([`adr/0024`](../../../../../../docs/adr/0024-base-store-versus-optional-feature.md)),
the schema change is a migration, and the rows are not.

The Feature's `DeliverySettings` collapses into `FulfillmentSettings`, which
already carried the two questions the base store had always answered — whether
the store delivers, and the floor beneath which it will not. The Feature added a
pause switch and a default minimum, and those are the two fields that move.

Safe to run twice: zones are matched on name, which is what the store's own
partial unique constraint already treats as identity for an active zone.
"""

from __future__ import annotations

from decimal import Decimal

from django.apps import apps as django_apps
from django.core.management.base import BaseCommand
from django.db import transaction

from apps.fulfillment.models import DeliveryZone, FulfillmentSettings

FEATURE_APP = "knight_feature_delivery"

COPIED_FIELDS = (
    "name",
    "fee",
    "minimum_order_subtotal",
    "status",
    "display_order",
    "archived_at",
)


class Command(BaseCommand):
    help = "Copies delivery zones and settings from the legacy Feature into the base store."

    def add_arguments(self, parser):
        parser.add_argument(
            "--dry-run",
            action="store_true",
            help="Report what would move without writing anything.",
        )
        parser.add_argument(
            "--enable-delivery",
            action="store_true",
            help=(
                "Also switch delivery on. A store that had the Feature was "
                "delivering, but the base store's own switch may never have been set."
            ),
        )

    def handle(self, *args, **options):
        dry_run = options["dry_run"]

        if not django_apps.is_installed(FEATURE_APP):
            self.stdout.write(
                "The legacy delivery Feature is not installed on this store; nothing to absorb."
            )
            return

        try:
            from knight_feature_delivery.models import DeliverySettings as OldSettings
            from knight_feature_delivery.models import DeliveryZone as OldZone
        except ImportError:
            self.stdout.write(
                "The delivery Feature on this store carries no zones; nothing to absorb."
            )
            return

        moved = 0
        skipped = 0

        with transaction.atomic():
            old_settings = OldSettings.objects.filter(pk=1).first()
            settings = FulfillmentSettings.current()

            if old_settings is not None:
                settings.delivery_accepting_orders = old_settings.is_accepting_orders

                # Null and zero both mean "no minimum" - the Feature said it one
                # way and the base store says it the other, so the reading is
                # preserved rather than the value.
                settings.delivery_minimum_order = (
                    old_settings.default_minimum_order
                    if old_settings.default_minimum_order is not None
                    else Decimal("0")
                )

            if options["enable_delivery"]:
                settings.delivery_enabled = True

            settings.save()

            for old in OldZone.objects.all().order_by("pk"):
                if DeliveryZone.objects.filter(name=old.name).exists():
                    skipped += 1
                    continue

                DeliveryZone(**{field: getattr(old, field) for field in COPIED_FIELDS}).save()
                moved += 1

            if dry_run:
                transaction.set_rollback(True)

        self.stdout.write(f"zones: {moved} moved, {skipped} already present")

        if not settings.delivery_enabled:
            # Worth saying out loud: the zones are in place and quoting will
            # still refuse every one of them until this is switched on, which
            # would otherwise look like the absorption having failed.
            self.stdout.write(
                self.style.WARNING(
                    "Delivery is switched off on this store, so no zone will quote. "
                    "Re-run with --enable-delivery, or set it on the settings screen."
                )
            )

        if dry_run:
            self.stdout.write(self.style.WARNING("Dry run - nothing was written."))
        else:
            self.stdout.write(
                self.style.SUCCESS(
                    "Absorbed. The Feature's own tables are untouched and go when it is uninstalled."
                )
            )
