"""
Turns paid subscription periods into real orders.

The seam between `subscriptions` and the store's orders, and the direction is the
store's: the Feature may not create an order — orders are the store's, and a
Feature that wrote them would be one the store could not uninstall
(`docs/adr/0024-base-store-versus-optional-feature.md`). So the Feature names the
periods that have been paid and have no order yet, this command creates them, and
the order number goes back.

**Both shapes of the same Feature.** `subscriptions` 1.x is a package installed
into this store, and 2.x is a service the store talks to over HTTP
(`docs/adr/0033-api-driven-features.md`). The loop is the same either way and so
is this command: it asks whichever one is present what is outstanding, places the
orders, and reports the numbers back. What changes is a function call becoming a
signed request, and nothing else — the store's own code below does not know which
it is talking to until it looks.

Idempotent on both sides, and it has to be. A merchant runs this from cron; a
second run that made a second order would send a shopper two boxes for one
payment. The Feature refuses to record a second order for a period, this command
asks the Feature what is outstanding rather than deciding for itself, and the
order it makes carries the Feature's own reference — so a run that placed an
order and then failed to report it finds that order again instead of placing
another.

The money on the order comes from the **period**, not from today's prices. A
shopper who subscribed at last year's price is owed last year's price, and the
order that documents it has to say so.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass, field
from decimal import Decimal, InvalidOperation

from django.apps import apps as django_apps
from django.core.management.base import BaseCommand
from django.db import transaction

from apps.orders.models import Order, OrderItem, OrderParty, OrderStatus

logger = logging.getLogger(__name__)

FEATURE_APP = "knight_feature_subscriptions"
FEATURE_SLUG = "subscriptions"

#: Where the service names what it is owed. On the Feature's staff surface,
#: which is what the store is here: it calls as itself, not as a shopper.
AWAITING_PATH = "/api/v1/admin/awaiting-orders/"


@dataclass
class Line:
    """One thing a period is for, in the only shape this command needs."""

    name: str
    quantity: int
    unit_price: Decimal
    source_product_id: int = 0
    source_variant_id: int | None = None


@dataclass
class Owed:
    """
    One paid period waiting for an order, from either shape of the Feature.

    `reference` is the Feature's own string and this store never interprets it.
    It goes onto the order's `external_reference` untouched and comes back to the
    Feature when the order is announced, which is what lets the Feature match an
    order to the exact period it paid for rather than guessing at the oldest one
    outstanding.
    """

    reference: str
    currency: str
    display_name: str = ""
    email: str = ""
    source_shopper_id: int | None = None
    lines: list[Line] = field(default_factory=list)

    #: How to tell the Feature which order this became. Set by whichever reader
    #: built this row, because the two do it differently.
    report: object = None

    def __str__(self) -> str:
        return self.reference


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
        limit = max(1, options["limit"])
        owed = self._installed(limit)

        if owed is None:
            owed = self._service(limit)

        if owed is None:
            # Not an error. A store without subscriptions, in either shape, has
            # no periods to turn into anything.
            self.stdout.write(
                "The subscriptions Feature is not on this store; nothing to generate."
            )
            return

        created = 0
        reported = 0

        for period in owed:
            if not period.lines:
                # A subscription with no lines is money taken for nothing named.
                # It is a data problem worth reporting rather than an empty order
                # worth creating.
                self.stdout.write(
                    self.style.WARNING(f"  {period.reference} has no lines; skipped.")
                )
                continue

            if options["dry_run"]:
                self.stdout.write(f"  would create an order for {period.reference}")
                continue

            existing = Order.objects.filter(external_reference=period.reference).first()

            if existing is not None:
                # The order was made and the Feature was not told: this command
                # died in between, or the report failed. Telling it now is the
                # whole reason the order carries the reference.
                if self._report(period, existing.number):
                    reported += 1
                    self.stdout.write(
                        f"  {period.reference} -> order {existing.number} (already placed)"
                    )

                continue

            with transaction.atomic():
                order = self._place(period)

            created += 1

            if self._report(period, order.number):
                reported += 1

            self.stdout.write(f"  {period.reference} -> order {order.number}")

        if options["dry_run"]:
            self.stdout.write(
                self.style.WARNING(f"{len(owed)} period(s) waiting. Dry run - nothing created.")
            )
            return

        self.stdout.write(
            self.style.SUCCESS(f"Created {created} order(s); reported {reported} back.")
        )

    # --- Where the outstanding periods come from -----------------------------

    def _installed(self, limit: int) -> list[Owed] | None:
        """`subscriptions` 1.x: a package running in this store's own process."""
        if not django_apps.is_installed(FEATURE_APP):
            return None

        from knight_feature_subscriptions import services

        owed = []

        for period in services.periods_awaiting_orders(limit=limit):
            subscription = period.subscription
            reference, sequence = subscription.reference, period.sequence

            owed.append(
                Owed(
                    # The same shape 2.x hands over, built here because 1.x runs
                    # in this process and has no reason to build strings for a
                    # caller it can call. It must name the **period**: a
                    # reference that named only the subscription would make the
                    # second period's order look like the first one's, already
                    # placed.
                    reference=f"{reference}#{sequence}",
                    currency=period.currency,
                    display_name=subscription.display_name,
                    email=subscription.email,
                    source_shopper_id=subscription.source_shopper_id,
                    lines=[
                        Line(
                            name=line.name,
                            quantity=line.quantity,
                            unit_price=line.unit_price,
                            source_product_id=line.source_product_id or 0,
                            source_variant_id=line.source_variant_id,
                        )
                        for line in subscription.lines.all()
                    ],
                    report=(
                        lambda number, reference=reference, sequence=sequence: services.record_order(
                            reference, sequence, number
                        )
                    ),
                )
            )

        return owed

    def _service(self, limit: int) -> list[Owed] | None:
        """
        `subscriptions` 2.x: a service, reached with a signed request.

        The store calls as itself — staff, subject `system` — because there is no
        shopper here, and asserting one would be telling the service something
        untrue.
        """
        from knight_integration.external import ServiceCallFailed, call, contract_for

        contract = contract_for(FEATURE_SLUG)

        if contract is None:
            return None

        try:
            answer = call(contract, "GET", f"{AWAITING_PATH}?limit={limit}")
        except ServiceCallFailed as failure:
            # Not a traceback. The store is fine and the service is not, and a
            # cron job that shouted a stack trace at a merchant would bury the
            # one line that says which.
            self.stderr.write(self.style.ERROR(f"  {failure}"))
            return []

        owed = []

        for item in answer.get("items") or []:
            reference = str(item.get("orderReference") or "").strip()

            if not reference:
                continue

            shopper = item.get("shopper") or {}

            owed.append(
                Owed(
                    reference=reference,
                    currency=str(item.get("currency") or "IRR"),
                    display_name=str(shopper.get("displayName") or ""),
                    email=str(shopper.get("email") or ""),
                    source_shopper_id=shopper.get("id"),
                    lines=[
                        Line(
                            name=str(line.get("name") or "Subscription"),
                            quantity=max(1, int(line.get("quantity") or 1)),
                            unit_price=_money(line.get("unitPrice")),
                            source_product_id=int(line.get("sourceProductId") or 0),
                            source_variant_id=line.get("sourceVariantId"),
                        )
                        for line in item.get("lines") or []
                    ],
                    report=_reporter(contract, item),
                )
            )

        return owed

    def _report(self, period: Owed, number: int) -> bool:
        """
        Tells the Feature which order a period became.

        A failure here is not a failure of the run. The order exists, it carries
        the Feature's reference, and both the store's next run and the Feature's
        own `order.placed` delivery will close the loop — so the right thing is
        to say so and keep going rather than to stop and leave the rest of a
        merchant's morning unplaced.
        """
        if period.report is None:
            return False

        try:
            period.report(number)
        except Exception as failure:  # noqa: BLE001 - see the docstring
            logger.warning(
                "Reporting order %s for %s failed: %s", number, period.reference, failure
            )
            self.stderr.write(
                self.style.WARNING(
                    f"  order {number} was placed for {period.reference} and the Feature "
                    f"was not told: {failure}"
                )
            )
            return False

        return True

    # --- The order itself ----------------------------------------------------

    def _place(self, period: Owed) -> Order:
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
            # Opaque, and carried rather than read. It is what the announcement
            # of this order hands back to the Feature, and the only thing that
            # lets the Feature know which period it was for.
            external_reference=period.reference[:100],
        )

        for index, line in enumerate(period.lines):
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

        if period.display_name or period.email:
            OrderParty.objects.create(
                order=order,
                source_shopper_id=period.source_shopper_id,
                display_name=period.display_name or "Subscriber",
                email=period.email,
            )

        order.recalculate()
        order.save(update_fields=["subtotal", "discount_total", "total", "updated_at"])
        order.transition_to(
            OrderStatus.CONFIRMED,
            actor="subscriptions",
            reason=period.reference,
        )

        return order


def _reporter(contract, item: dict):
    """
    The call that tells a service which order a period became.

    Built here rather than in the loop so the path is worked out once, from the
    two halves the service reported — and so the loop below has one shape for
    both kinds of Feature.
    """
    from knight_integration.external import call

    reference = str(item.get("reference") or "")
    sequence = int(item.get("sequence") or 0)
    path = f"/api/v1/admin/{reference}/periods/{sequence}/order/"

    def report(number: int) -> None:
        call(contract, "POST", path, {"orderNumber": int(number)})

    return report


def _money(value) -> Decimal:
    try:
        return Decimal(str(value if value is not None else "0"))
    except (InvalidOperation, ValueError):
        # A price this store cannot read is not a price to guess at. Zero is
        # visible in the order total, which is where somebody will notice it.
        return Decimal("0")
