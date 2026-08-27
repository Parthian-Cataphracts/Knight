"""
Decides which branch handles each confirmed order.

The seam between the store's orders and `multi-location`, and the direction is
the store's: the Feature may not read `apps.orders`, so the store hands over the
order number and the delivery address and gets a branch back. The order is not
modified — where it is handled is recorded by the Feature, against the order
*number*, which is what keeps the record readable after the Feature is gone
(`docs/adr/0024-base-store-versus-optional-feature.md`).

Idempotent, and by construction rather than by care: `route()` decides once and
returns the same decision afterwards, so running this twice — or every minute
from cron, which is how a merchant will run it — routes nothing twice.

An order nowhere can take is left alone rather than forced somewhere. Every
branch being shut is a real state at two in the morning, and an order pushed to a
closed kitchen is one nobody cooks; leaving it unrouted means the next run picks
it up when somebody opens.
"""

from __future__ import annotations

from django.apps import apps as django_apps
from django.core.management.base import BaseCommand

from apps.orders.models import Order, OrderStatus

FEATURE_APP = "knight_feature_multi_location"


class Command(BaseCommand):
    help = "Routes every confirmed order to a location, if multi-location is installed."

    def add_arguments(self, parser):
        parser.add_argument(
            "--limit",
            type=int,
            default=200,
            help="How many orders to look at in one run. A backlog is worked through over several.",
        )

    def handle(self, *args, **options):
        if not django_apps.is_installed(FEATURE_APP):
            # Not an error. A single-site merchant routes nothing, because there
            # is nowhere else for an order to go.
            self.stdout.write(
                "The multi-location Feature is not installed on this store; nothing to route."
            )
            return

        from knight_feature_multi_location import services

        orders = (
            Order.objects.filter(
                status__in=[OrderStatus.CONFIRMED, OrderStatus.PREPARING]
            )
            .select_related("fulfillment")
            .order_by("number")[: max(1, options["limit"])]
        )

        routed = held = 0

        for order in orders:
            fulfillment = getattr(order, "fulfillment", None)

            try:
                decision = services.route(
                    order.number,
                    postal_code=getattr(fulfillment, "postal_code", "") or "",
                    city=getattr(fulfillment, "city", "") or "",
                    zone=getattr(fulfillment, "delivery_zone_name", "") or "",
                )
            except services.NowhereToRouteTo:
                # Left for the next run. See the module docstring: an order sent
                # to a shut branch is an order nobody cooks.
                held += 1
                continue

            routed += 1
            self.stdout.write(f"  order {order.number} -> {decision.location} ({decision.reason})")

        self.stdout.write(self.style.SUCCESS(f"Routed {routed} order(s)."))

        if held:
            self.stdout.write(
                f"{held} order(s) had nowhere open to go and were left for the next run."
            )
