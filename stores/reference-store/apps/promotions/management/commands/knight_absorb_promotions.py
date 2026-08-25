"""
Moves promotion rows out of the old promotions Feature and into the base store.

Phase 12 moved coupons and discounts from an optional Feature into the base
image ([`adr/0024`](../../../../../../docs/adr/0024-base-store-versus-optional-feature.md)).
The schema change is a migration; the *rows* are not, and this is them.

Why a command rather than a data migration: a data migration in `apps.promotions`
would run on every store, including the overwhelming majority that never had the
Feature installed, and would have to reach into tables belonging to an app that
may not be in `INSTALLED_APPS`. Django gives no honest way to express "copy from
a table that might not exist". An operator running one command against one store,
reading what it did, is the truthful shape.

Safe to run twice. Rows are matched on what makes a promotion the same promotion
— a coupon's normalised code, and a promotion's name and window — so a second
run reports what it skipped rather than duplicating a campaign.
"""

from __future__ import annotations

from django.apps import apps as django_apps
from django.core.management.base import BaseCommand
from django.db import transaction

from apps.promotions.models import Coupon, CouponRedemption, Promotion

FEATURE_APP = "knight_feature_promotions"

COPIED_FIELDS = (
    "name",
    "description",
    "status",
    "discount_type",
    "discount_value",
    "minimum_subtotal",
    "maximum_discount_amount",
    "starts_at",
    "ends_at",
    "requires_coupon",
    "priority",
    "archived_at",
)


class Command(BaseCommand):
    help = "Copies promotions, coupons and redemptions from the legacy Feature into the base store."

    def add_arguments(self, parser):
        parser.add_argument(
            "--dry-run",
            action="store_true",
            help="Report what would move without writing anything.",
        )

    def handle(self, *args, **options):
        dry_run = options["dry_run"]

        if not django_apps.is_installed(FEATURE_APP):
            # Not an error. The overwhelming majority of stores never had the
            # Feature, and for them there is simply nothing to move.
            self.stdout.write(
                "The legacy promotions Feature is not installed on this store; nothing to absorb."
            )
            return

        try:
            from knight_feature_promotions.models import Coupon as OldCoupon
            from knight_feature_promotions.models import Promotion as OldPromotion
        except ImportError:
            # The Feature is installed but is already 2.0.0, which does not have
            # these models: this store has been through the move. Saying so beats
            # an ImportError traceback, which is what an operator running the
            # command twice would otherwise be handed.
            self.stdout.write(
                "The promotions Feature on this store is already 2.0.0 and owns no coupons; "
                "nothing to absorb."
            )
            return

        moved = {"promotions": 0, "coupons": 0, "redemptions": 0}
        skipped = {"promotions": 0, "coupons": 0, "redemptions": 0}

        with transaction.atomic():
            for old in OldPromotion.objects.all().order_by("pk"):
                existing = Promotion.objects.filter(
                    name=old.name, starts_at=old.starts_at, ends_at=old.ends_at
                ).first()

                if existing is not None:
                    skipped["promotions"] += 1
                    new = existing
                else:
                    new = Promotion(**{field: getattr(old, field) for field in COPIED_FIELDS})
                    new.save()
                    moved["promotions"] += 1

                for old_coupon in OldCoupon.objects.filter(promotion_id=old.pk).order_by("pk"):
                    if Coupon.objects.filter(normalized_code=old_coupon.normalized_code).exists():
                        skipped["coupons"] += 1
                        continue

                    coupon = Coupon(
                        promotion=new,
                        code=old_coupon.code,
                        status=old_coupon.status,
                        usage_limit_total=old_coupon.usage_limit_total,
                        starts_at=old_coupon.starts_at,
                        ends_at=old_coupon.ends_at,
                        archived_at=old_coupon.archived_at,
                    )
                    coupon.save()
                    moved["coupons"] += 1

                    # Redemptions decide whether a limited campaign is exhausted.
                    # Losing them would silently hand every used-up coupon back
                    # to the next shopper who tries it.
                    for old_redemption in old_coupon.redemptions.all().order_by("pk"):
                        _, created = CouponRedemption.objects.get_or_create(
                            coupon=coupon, source_order_id=old_redemption.source_order_id
                        )

                        if created:
                            moved["redemptions"] += 1
                        else:
                            skipped["redemptions"] += 1

            if dry_run:
                transaction.set_rollback(True)

        for kind in ("promotions", "coupons", "redemptions"):
            self.stdout.write(
                f"{kind}: {moved[kind]} moved, {skipped[kind]} already present"
            )

        if dry_run:
            self.stdout.write(self.style.WARNING("Dry run - nothing was written."))
        else:
            self.stdout.write(
                self.style.SUCCESS(
                    "Absorbed. The Feature's own tables are untouched and go when it is uninstalled."
                )
            )
