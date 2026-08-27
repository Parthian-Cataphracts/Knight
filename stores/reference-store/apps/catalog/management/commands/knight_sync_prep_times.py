"""
Gives every sellable variant in this store a preparation profile in
`restaurant-operations`.

The same seam as `knight_sync_inventory` and `knight_reindex_search`, and the
direction is the same: the Feature may not read `apps.catalog`, so the store —
which owns the menu — hands the definitions over. Neither side sees the other's
models, which is what makes the Feature removable.

It defines profiles and touches no ticket. A dish renamed or re-timed this
afternoon is corrected here; the tickets already printed keep the minutes they
were opened with, because how long this morning's service actually took is not
changed by a recipe being edited afterwards.

Variants with no SKU are skipped and counted, for the reason the inventory sync
gives: a thing with no identity cannot be matched to anything later.
"""

from __future__ import annotations

from django.apps import apps as django_apps
from django.core.management.base import BaseCommand

from apps.catalog.models import ProductStatus, ProductVariant

FEATURE_APP = "knight_feature_restaurant_operations"


class Command(BaseCommand):
    help = "Defines a preparation profile for every SKU on the menu, if restaurant-operations is installed."

    def add_arguments(self, parser):
        parser.add_argument(
            "--minutes",
            type=int,
            default=None,
            help=(
                "Set this preparation time on every profile. Omitted, each keeps the one it has, "
                "because how long a dish takes is the kitchen's judgement and not the till's."
            ),
        )
        parser.add_argument(
            "--station",
            default="",
            help="Route every profile to this station code. Omitted, existing routing is kept.",
        )

    def handle(self, *args, **options):
        if not django_apps.is_installed(FEATURE_APP):
            # Not an error. Restaurant operations is optional, and a store
            # without it takes orders exactly as it did before.
            self.stdout.write(
                "The restaurant-operations Feature is not installed on this store; nothing to sync."
            )
            return

        from knight_feature_restaurant_operations import services

        defined = skipped = 0

        for variant in ProductVariant.objects.select_related("product").iterator():
            if not variant.sku.strip():
                skipped += 1
                continue

            services.define_prep(
                variant.sku,
                name=f"{variant.product.name} — {variant.name}",
                station=options["station"],
                object_id=variant.pk,
                prep_minutes=options["minutes"],
                # A variant that cannot be sold keeps its profile and stops being
                # something the kitchen is asked to make, which is what a
                # restaurant means by "that is off tonight" rather than "that was
                # never on the menu".
                is_prepared=variant.is_available and variant.product.status == ProductStatus.ACTIVE,
            )
            defined += 1

        self.stdout.write(self.style.SUCCESS(f"Defined {defined} preparation profile(s)."))

        if skipped:
            self.stdout.write(
                f"Skipped {skipped} variant(s) with no SKU; give them one to time their preparation."
            )
