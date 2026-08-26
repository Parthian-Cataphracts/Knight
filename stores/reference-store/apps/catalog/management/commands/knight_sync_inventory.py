"""
Gives every sellable variant in this store a stock item in `advanced-inventory`.

The same seam as `knight_reindex_search`, and the direction is the same: the
Feature may not read `apps.catalog`, so the store — which owns the catalogue and
knows what is worth tracking in it — hands the definitions over. Neither side
sees the other's models, which is what makes the Feature removable.

It defines items and never touches movements. A variant renamed or repriced is
corrected here; the ledger underneath it is untouched, because what happened to
the stock happened whatever the product is called now.

Variants with no SKU are skipped and counted. A shop that has not given
something a SKU has not given it an identity to track stock against, and
inventing one here would produce an item nobody can match to anything.
"""

from __future__ import annotations

from django.apps import apps as django_apps
from django.core.management.base import BaseCommand

from apps.catalog.models import ProductStatus, ProductVariant

FEATURE_APP = "knight_feature_advanced_inventory"


class Command(BaseCommand):
    help = "Defines a stock item for every SKU in the catalogue, if advanced-inventory is installed."

    def add_arguments(self, parser):
        parser.add_argument(
            "--reorder-point",
            default=None,
            help=(
                "Set this reorder point on every item. Omitted, each item keeps the one it has, "
                "because a reorder point is the merchant's judgement about their own shop."
            ),
        )

    def handle(self, *args, **options):
        if not django_apps.is_installed(FEATURE_APP):
            # Not an error. Inventory is optional, and a store without it sells
            # exactly as it did before.
            self.stdout.write(
                "The advanced-inventory Feature is not installed on this store; nothing to sync."
            )
            return

        from knight_feature_advanced_inventory import services

        defined = skipped = 0

        for variant in ProductVariant.objects.select_related("product").iterator():
            if not variant.sku.strip():
                skipped += 1
                continue

            services.define_item(
                variant.sku,
                name=f"{variant.product.name} — {variant.name}",
                object_id=variant.pk,
                # Untracked rather than absent for a variant that cannot be sold.
                # It keeps its identity and its history and stays out of alerts,
                # which is what a merchant means by "we do not stock that any
                # more" rather than "that never existed".
                is_tracked=variant.is_available and variant.product.status == ProductStatus.ACTIVE,
                reorder_point=options["reorder_point"],
            )
            defined += 1

        self.stdout.write(self.style.SUCCESS(f"Defined {defined} stock item(s)."))

        if skipped:
            self.stdout.write(
                f"Skipped {skipped} variant(s) with no SKU; give them one to track their stock."
            )
