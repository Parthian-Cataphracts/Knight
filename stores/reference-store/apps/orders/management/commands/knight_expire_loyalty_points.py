"""
Writes off loyalty points whose expiry has passed.

The scheduled half of `loyalty-rewards`, and it lives in the store rather than
in the Feature because the manifest schema has no concept of a scheduled job
yet. Declaring one in the manifest would look like a guarantee and schedule
nothing, so this is a command the store's own cron runs — daily is right, and
the run is idempotent, so an extra run costs a query and changes nothing.

Phase 15 needs manifest-declared workers for real, because marketing-automation
cannot work without them. When that arrives this command becomes the thing the
worker calls rather than the thing an operator remembers.
"""

from __future__ import annotations

from django.apps import apps as django_apps
from django.core.management.base import BaseCommand

FEATURE_APP = "knight_feature_loyalty_rewards"


class Command(BaseCommand):
    help = "Expires loyalty point lots that have passed their expiry date."

    def add_arguments(self, parser):
        parser.add_argument(
            "--dry-run",
            action="store_true",
            help="Report what would expire without writing anything.",
        )

    def handle(self, *args, **options):
        if not django_apps.is_installed(FEATURE_APP):
            # Not an error. Loyalty is optional, and a store without it has
            # nothing to expire.
            self.stdout.write(
                "The loyalty-rewards Feature is not installed on this store; nothing to expire."
            )
            return

        from django.db import transaction as db_transaction

        from knight_feature_loyalty_rewards import services

        if options["dry_run"]:
            # Rolled back rather than counted separately, so the dry run and the
            # real run go down exactly the same code path. A dry run that used a
            # different query is a dry run that can disagree with the thing it
            # is previewing.
            with db_transaction.atomic():
                removed = services.expire_stale()
                db_transaction.set_rollback(True)

            self.stdout.write(f"{removed} point(s) would expire.")
            self.stdout.write(self.style.WARNING("Dry run - nothing was written."))
            return

        removed = services.expire_stale()

        self.stdout.write(self.style.SUCCESS(f"Expired {removed} point(s)."))
