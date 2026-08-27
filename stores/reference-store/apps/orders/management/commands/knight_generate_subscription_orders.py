"""
Turns paid subscription periods into real orders.

The seam between `subscriptions` and the store's orders, and the direction is the
store's: the Feature may not create an order — orders are the store's, and a
Feature that wrote them would be one the store could not uninstall
(`docs/adr/0024-base-store-versus-optional-feature.md`). So the Feature names the
periods that have been paid and have no order yet, this command creates them, and
the order number goes back.

Idempotent on both sides, and it has to be. A merchant runs this from cron; a
second run that made a second order would send a shopper two boxes for one
payment. The Feature refuses to record a second order for a period, and this
command asks the Feature what is outstanding rather than deciding for itself.

The money on the order comes from the **period**, not from today's prices. A
shopper who subscribed at last year's price is owed last year's price, and the
order that documents it has to say so.
"""

from __future__ import annotations

from decimal import Decimal

from django.apps import apps as django_apps
from django.core.management.base import BaseCommand
from django.db import transaction

from apps.orders.models import Order, OrderItem, OrderParty, OrderStatus

FEATURE_APP = "knight_feature_subscriptions"


class Command(BaseCommand):
    help = "Creates an order for every paid subscription period that has not got one."

    def add_arguments(self, parser):
        parser.add_argument(
            "--limit",
            type=int,
            default=200,
            help="How many periods to look at in one run. A backlog is worked through over several.",
        )
        parser.add_argument(
            "--dry-run",
            action="store_true",
            help="List what would be created without creating anything.",
        )

    def handle(self, *args, **options):
        if not django_apps.is_installed(FEATURE_APP):
            # Not an error. A store without subscriptions has no periods to turn
            # into anything.
            self.stdout.write(
                "The subscriptions Feature is not installed on this store; nothing to generate."
            )
            return

        from knight_feature_subscriptions import services

        periods = services.periods_awaiting_orders(limit=max(1, options["limit"]))
        created = 0

        for period in periods:
            subscription = period.subscription
            lines = list(subscription.lines.all())

            if not lines:
                # A subscription with no lines is money taken for nothing named.
                # It is a data problem worth reporting rather than an empty order
                # worth creating.
                self.stdout.write(
                    self.style.WARNING(
                        f"  {subscription.reference} period {period.sequence} has no lines; skipped."
                    )
                )
                continue

            if options["dry_run"]:
                self.stdout.write(f"  would create an order for {subscription.reference} #{period.sequence}")
                continue

            with transaction.atomic():
                order = self._place(subscription, period, lines)
                services.record_order(subscription.reference, period.sequence, order.number)

            created += 1
            self.stdout.write(f"  {subscription.reference} #{period.sequence} -> order {order.number}")

        if options["dry_run"]:
            self.stdout.write(self.style.WARNING(f"{len(periods)} period(s) waiting. Dry run - nothing created."))
            return

        self.stdout.write(self.style.SUCCESS(f"Created {created} order(s)."))

    def _place(self, subscription, period, lines) -> Order:
        """
        One order for one period, priced as the period was.

        Placed `confirmed` rather than `pending`: the money is already taken, so
        an order waiting for payment would be a lie the rest of the store would
        act on.
        """
        order = Order.place(
            subtotal=Decimal("0"),
            total=Decimal("0"),
            currency=period.currency,
        )

        for index, line in enumerate(lines):
            item = OrderItem(
                order=order,
                source_product_id=line.source_product_id or 0,
                source_variant_id=line.source_variant_id,
                product_name=line.name,
                unit_base_price=line.unit_price,
                quantity=line.quantity,
                display_order=index,
            )
            item.price()
            item.save()

        if subscription.display_name or subscription.email:
            OrderParty.objects.create(
                order=order,
                source_shopper_id=subscription.source_shopper_id,
                display_name=subscription.display_name or "Subscriber",
                email=subscription.email,
            )

        order.recalculate()
        order.save(update_fields=["subtotal", "discount_total", "total", "updated_at"])
        order.transition_to(
            OrderStatus.CONFIRMED,
            actor="subscriptions",
            reason=f"{subscription.reference} period {period.sequence}",
        )

        return order
