"""
Puts confirmed orders in front of the kitchen.

The seam between the store's orders and `restaurant-operations`, and the
direction is the store's: the Feature may not read `apps.orders`, so the store
hands over what was ordered and gets a ticket number back. The order is not
modified — its status is the shopper's, and the ticket's state is the kitchen's
(`docs/adr/0024-base-store-versus-optional-feature.md`).

Idempotent, and it has to be. A restaurant runs this from a cron entry every
minute; a second run that printed the same order again would be a second burger
on the grill. An order that already has a ticket is skipped by looking the number
up in the Feature, which is the only place that knows.

Modifiers become the ticket line's modifications — "no onions", "sauce on the
side". They are the most important words on a restaurant ticket and the ones most
often lost between the till and the pass, so they are carried across explicitly
rather than left to a note field.
"""

from __future__ import annotations

from django.apps import apps as django_apps
from django.core.management.base import BaseCommand

from apps.orders.models import FulfillmentMethod, Order, OrderStatus

FEATURE_APP = "knight_feature_restaurant_operations"

#: How an order's fulfilment method reads to a kitchen. Collection and delivery
#: are promised to somebody who is not in the building; anything else is eaten
#: here.
SERVICE_BY_METHOD = {
    FulfillmentMethod.COLLECTION: "collection",
    FulfillmentMethod.DELIVERY: "delivery",
}


class Command(BaseCommand):
    help = "Opens a kitchen ticket for every confirmed order that has not got one."

    def add_arguments(self, parser):
        parser.add_argument(
            "--location",
            default="",
            help="The location code to stamp on the tickets. Empty for a restaurant with one kitchen.",
        )
        parser.add_argument(
            "--limit",
            type=int,
            default=200,
            help="How many orders to look at in one run. A backlog is worked through over several.",
        )

    def handle(self, *args, **options):
        if not django_apps.is_installed(FEATURE_APP):
            # Not an error. A shop that is not a restaurant takes orders exactly
            # as it did before.
            self.stdout.write(
                "The restaurant-operations Feature is not installed on this store; nothing to print."
            )
            return

        from knight_feature_restaurant_operations import services
        from knight_feature_restaurant_operations.models import KitchenTicket

        candidates = (
            Order.objects.filter(
                status__in=[OrderStatus.CONFIRMED, OrderStatus.PREPARING]
            )
            .select_related("fulfillment")
            .prefetch_related("items__modifiers")
            .order_by("number")[: max(1, options["limit"])]
        )
        candidates = list(candidates)

        already = set(
            KitchenTicket.objects.filter(
                source_order_number__in=[order.number for order in candidates]
            ).values_list("source_order_number", flat=True)
        )

        printed = skipped = 0

        for order in candidates:
            if order.number in already:
                skipped += 1
                continue

            method = getattr(getattr(order, "fulfillment", None), "method", None)
            lines = list(order.items.all())

            if not lines:
                # An order with nothing on it is a data problem, not a ticket.
                # Printing an empty one would put a blank on the board that
                # nobody can clear.
                skipped += 1
                continue

            ticket = services.open_ticket(
                [
                    {
                        "object_id": item.source_variant_id or item.source_product_id,
                        "name": item.product_name,
                        "quantity": item.quantity,
                        "source_order_item_id": item.pk,
                        "modifications": ", ".join(
                            modifier.modifier_name for modifier in item.modifiers.all()
                        ),
                    }
                    for item in lines
                ],
                order_number=order.number,
                service=SERVICE_BY_METHOD.get(method, "dine-in"),
                location=options["location"],
            )
            printed += 1
            self.stdout.write(f"  order {order.number} -> ticket {ticket.number}")

        self.stdout.write(self.style.SUCCESS(f"Printed {printed} ticket(s)."))

        if skipped:
            self.stdout.write(f"{skipped} order(s) already had one; a second run prints nothing twice.")
