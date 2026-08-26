"""
Pushes this store's catalogue into the `advanced-search` index.

The seam, and the direction of it is the point. The Feature may not read
`apps.catalog` — a Feature never imports store business code — so it cannot index
anything on its own. The store, which owns the catalogue and knows what is worth
searching in it, hands documents over. Neither side sees the other's models.

That also means this command is the store's, not the Feature's, and it is
allowed to know both: it reads catalogue models directly and calls the Feature
through its published service surface.

Run it after a bulk import, or on a schedule. A store that edits products
through an admin screen would call `services.index()` from a signal instead;
this exists because a full rebuild is the honest way to recover from an index
that has drifted, and because there is no admin screen yet.
"""

from __future__ import annotations

from django.apps import apps as django_apps
from django.core.management.base import BaseCommand

from apps.catalog.models import Product, ProductStatus

FEATURE_APP = "knight_feature_advanced_search"


class Command(BaseCommand):
    help = "Indexes the catalogue into the advanced-search Feature, if it is installed."

    def add_arguments(self, parser):
        parser.add_argument(
            "--rebuild",
            action="store_true",
            help="Empty the index first, so products deleted since the last run disappear.",
        )

    def handle(self, *args, **options):
        if not django_apps.is_installed(FEATURE_APP):
            # Not an error. Search is optional, and a store without it has a
            # catalogue that works exactly as before.
            self.stdout.write(
                "The advanced-search Feature is not installed on this store; nothing to index."
            )
            return

        from knight_feature_advanced_search import services

        if options["rebuild"]:
            removed = services.clear()
            self.stdout.write(f"Cleared {removed} document(s).")

        documents = [
            {
                "object_id": product.pk,
                "object_type": "product",
                "title": product.name,
                "body": product.description,
                # The slug is what a shopper sees in a URL and often what they
                # half-remember, and it is not in the title once hyphens are
                # stripped out of it.
                "keywords": product.slug.replace("-", " "),
                "category_id": product.category_id,
                # Draft and archived products are indexed and marked unavailable
                # rather than skipped: a merchant searching their own catalogue
                # needs to find the draft they are about to publish, and the
                # storefront's default filter already hides it from shoppers.
                "is_available": product.status == ProductStatus.ACTIVE and product.is_visible,
            }
            for product in Product.objects.select_related("category").iterator()
        ]

        indexed = services.index_many(documents)

        self.stdout.write(self.style.SUCCESS(f"Indexed {indexed} product(s)."))

        stats = services.stats()
        self.stdout.write(f"Index now holds {stats.get('total', 0)} document(s).")
