"""
Queues this store's orders for the POS and accounting systems it is connected to.

The seam between the store's orders and `external-marketplaces`, and the
direction is the store's: the Feature may not read `apps.orders`, so the store
hands over what an order says and gets a queued message back
(`docs/adr/0024-base-store-versus-optional-feature.md`).

It **queues** and never sends. The Feature's own worker does the sending, on its
own schedule, with its own retries — so a partner being down slows nothing here
and this command is safe to run from cron every minute.

Idempotent by subject rather than by luck: an order already queued for a
connection is skipped. A second run that queued the same order again would put
two of the same invoice into an accounting system, which is the kind of thing a
merchant finds out about from their accountant.

Marketplaces are deliberately not pushed to. Orders come *from* a marketplace,
not to one, and a store that sent its own orders to a delivery marketplace would
be creating orders on somebody else's platform.
"""

from __future__ import annotations

from django.apps import apps as django_apps
from django.core.management.base import BaseCommand

from apps.orders.models import Order, OrderStatus

FEATURE_APP = "knight_feature_external_marketplaces"

#: The kinds of system an order is pushed to. See the module docstring for why
#: `marketplace` is not one of them.
RECEIVERS = ("pos", "accounting")


class Command(BaseCommand):
    help = "Queues every confirmed order for the POS and accounting connections that have not had it."

    def add_arguments(self, parser):
        parser.add_argument(
            "--limit",
            type=int,
            default=200,
            help="How many orders to look at in one run. A backlog is worked through over several.",
        )

    def handle(self, *args, **options):
        if not django_apps.is_installed(FEATURE_APP):
            # Not an error. A store with no integrations pushes nothing.
            self.stdout.write(
                "The external-marketplaces Feature is not installed on this store; nothing to push."
            )
            return

        from knight_feature_external_marketplaces import services

        receivers = [
            connection
            for kind in RECEIVERS
            for connection in services.connections(kind=kind, usable_only=True)
        ]

        if not receivers:
            self.stdout.write("No POS or accounting connection is switched on; nothing to push.")
            return

        orders = list(
            Order.objects.filter(
                status__in=[OrderStatus.CONFIRMED, OrderStatus.PREPARING, OrderStatus.READY, OrderStatus.COMPLETED]
            )
            .prefetch_related("items")
            .order_by("number")[: max(1, options["limit"])]
        )

        # Asked of the Feature rather than worked out from its tables. A store
        # reading a Feature's models is the one thing the delivery model does not
        # allow in either direction.
        already = services.already_queued("order", [order.number for order in orders])

        queued = skipped = 0

        for order in orders:
            for receiver in receivers:
                if (receiver["slug"], str(order.number)) in already:
                    skipped += 1
                    continue

                services.queue(
                    receiver["slug"],
                    kind="order.placed",
                    subject_type="order",
                    subject_id=order.number,
                    payload=self._describe(order),
                )
                queued += 1

        self.stdout.write(self.style.SUCCESS(f"Queued {queued} message(s)."))

        if skipped:
            self.stdout.write(f"{skipped} were already queued; a second run sends nothing twice.")

    def _describe(self, order: Order) -> dict:
        """
        The order as a partner sees it.

        Built here rather than in the Feature, because what an order *is* is the
        store's business — and because a payload assembled by the Feature would
        mean the Feature reading `apps.orders`, which is the one thing it may not
        do.
        """
        return {
            "orderNumber": order.number,
            "status": order.status,
            "currency": order.currency,
            "total": str(order.total),
            "placedAt": order.placed_at.isoformat(),
            "lines": [
                {
                    "sku": item.source_variant_id or item.source_product_id,
                    "name": item.product_name,
                    "quantity": item.quantity,
                    "unitPrice": str(item.unit_price),
                }
                for item in order.items.all()
            ],
        }
