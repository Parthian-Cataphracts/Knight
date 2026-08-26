"""
What a store may ask this Feature to do.

The published surface. Callers pass SKUs and quantities and get numbers and
plain dataclasses back; nothing here returns a model, and nothing here reads one
of the store's.

Three things in this module carry the weight:

- **`available()` is the number a shop may sell**, and it is on hand *minus what
  is held*. Selling against on-hand is how the last one of something gets sold
  twice while one of the two buyers is still typing their card number.
- **`reserve()` is the only function that has to be right under concurrency**,
  and it is the only one that takes a lock. Everything else appends to a ledger,
  where two writers cannot conflict by construction.
- **Nothing is ever recalculated into a stored total.** Every number this module
  returns is derived when it is asked for.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import timedelta
from decimal import Decimal

from django.db import transaction
from django.db.models import Q, QuerySet, Sum
from django.utils import timezone

from . import config
from .models import (
    AlertKind,
    DEFAULT_LOCATION,
    MovementReason,
    PurchaseOrder,
    PurchaseOrderLine,
    PurchaseOrderState,
    Reservation,
    ReservationState,
    StockAlert,
    StockItem,
    StockMovement,
    Supplier,
)

ZERO = Decimal("0.000")

#: Reasons that may only ever add stock, and reasons that may only ever remove
#: it. `adjustment` is in neither: a stocktake corrects in both directions, which
#: is the whole point of it.
INBOUND = {MovementReason.RECEIPT, MovementReason.RETURN, MovementReason.TRANSFER_IN}
OUTBOUND = {MovementReason.SALE, MovementReason.SHRINKAGE, MovementReason.TRANSFER_OUT}


class InventoryError(RuntimeError):
    """Something a caller asked for that this Feature will not do."""


class UnknownItem(InventoryError):
    """No stock item has that SKU."""


class NotEnoughStock(InventoryError):
    """
    The shop does not have enough to promise.

    Carries the numbers, because a caller showing "out of stock" and a caller
    showing "only 2 left" need the same refusal and different words.
    """

    def __init__(self, sku: str, requested: Decimal, available: Decimal) -> None:
        super().__init__(
            f"{sku}: {requested} requested and {available} available."
        )
        self.sku = sku
        self.requested = requested
        self.available = available


@dataclass(frozen=True)
class Level:
    """What there is of one item at one location."""

    sku: str
    name: str
    location: str
    on_hand: Decimal
    held: Decimal
    reorder_point: Decimal

    @property
    def available(self) -> Decimal:
        """
        What may be sold. Never negative in what it reports.

        On hand can genuinely go below zero — a shop that sold something it had
        already written off is a shop with a counting problem, and the ledger
        says so rather than hiding it. What must not happen is that number being
        offered to a shopper as though it meant anything.
        """
        return max(self.on_hand - self.held, ZERO)

    @property
    def is_low(self) -> bool:
        return self.available <= self.reorder_point


@dataclass(frozen=True)
class Suggestion:
    """One item worth reordering, and how much of it."""

    sku: str
    name: str
    available: Decimal
    reorder_point: Decimal
    suggested_quantity: Decimal
    supplier_code: str | None
    on_order: Decimal

    @property
    def has_a_quantity(self) -> bool:
        """
        Whether anybody has said how much to order.

        Zero is reported as zero rather than guessed at. A suggestion that
        invented "order 50" because the field was empty is a suggestion that gets
        acted on once and distrusted forever.
        """
        return self.suggested_quantity > ZERO


# --- Items and suppliers ----------------------------------------------------


def define_item(
    sku: str,
    *,
    name: str = "",
    object_id: int | None = None,
    unit: str = "each",
    supplier_code: str | None = None,
    reorder_point: Decimal | str | int | None = None,
    reorder_quantity: Decimal | str | int | None = None,
    is_tracked: bool = True,
) -> StockItem:
    """
    Creates or updates the definition of a tracked thing.

    Upserted on the SKU, which is the identity a store already has for it. Not
    the movements — those are never touched — so re-running a catalogue sync
    corrects names and leaves the history alone.

    `reorder_point` and `reorder_quantity` are left alone when they are not
    given, rather than reset to zero. They are a merchant's judgement about their
    own shop, and a nightly catalogue sync that quietly wiped them would silence
    every low-stock alert the merchant had set up, in a way that looks like
    nothing being low.
    """
    normalized = _sku(sku)

    if not normalized:
        raise InventoryError("A stock item needs a SKU.")

    supplier = None
    if supplier_code:
        supplier = Supplier.objects.filter(code=supplier_code).first()
        if supplier is None:
            raise InventoryError(f"No supplier has the code '{supplier_code}'.")

    defaults = {
        "name": name or normalized,
        "object_id": object_id,
        "unit": unit,
        "supplier": supplier,
        "is_tracked": is_tracked,
    }

    if reorder_point is not None:
        defaults["reorder_point"] = _quantity(reorder_point, allow_zero=True)

    if reorder_quantity is not None:
        defaults["reorder_quantity"] = _quantity(reorder_quantity, allow_zero=True)

    item, _ = StockItem.objects.update_or_create(sku=normalized, defaults=defaults)

    return item


def define_supplier(code: str, *, name: str = "", **fields) -> Supplier:
    """Creates or updates a supplier, keyed on the code the merchant uses."""
    if not code.strip():
        raise InventoryError("A supplier needs a code.")

    supplier, _ = Supplier.objects.update_or_create(
        code=code.strip(),
        defaults={"name": name or code.strip(), **fields},
    )

    return supplier


def find_items(text: str, *, limit: int = 20) -> list[StockItem]:
    """
    Items whose SKU or name is like what was typed.

    Trigram matching rather than `LIKE '%x%'`, because the person typing is a
    member of staff with a delivery in their hands and half a product name in
    their head. The GIN indexes on both columns are what make it answerable
    without scanning the catalogue
    (docs/adr/0031-database-extensions-are-declared-not-migrated.md).

    An exact SKU always wins and always comes first: somebody who typed a whole
    SKU knows what they want, and a similarity ranking that put something else
    above it would be actively unhelpful.
    """
    text = (text or "").strip()

    if not text:
        return []

    limit = max(1, min(limit, 100))
    exact = list(StockItem.objects.filter(sku=_sku(text))[:1])

    if len(text) < 3:
        # Below three characters trigram matching is noise, so a prefix is the
        # only honest answer.
        similar = StockItem.objects.filter(Q(sku__istartswith=text) | Q(name__istartswith=text))
    else:
        similar = StockItem.objects.filter(
            Q(sku__trigram_similar=text)
            | Q(name__trigram_similar=text)
            | Q(sku__icontains=text)
            | Q(name__icontains=text)
        )

    found = exact + [
        item for item in similar.order_by("sku")[: limit + 1] if not exact or item.pk != exact[0].pk
    ]

    return found[:limit]


def find_suppliers(text: str, *, limit: int = 20) -> list[Supplier]:
    """Suppliers whose name or code is like what was typed."""
    text = (text or "").strip()

    if not text:
        return []

    limit = max(1, min(limit, 100))

    if len(text) < 3:
        matches = Supplier.objects.filter(Q(code__istartswith=text) | Q(name__istartswith=text))
    else:
        matches = Supplier.objects.filter(
            Q(name__trigram_similar=text) | Q(name__icontains=text) | Q(code__icontains=text)
        )

    return list(matches.order_by("name")[:limit])


# --- The ledger -------------------------------------------------------------


def record(
    sku: str,
    quantity: Decimal | str | int,
    reason: str,
    *,
    location: str = DEFAULT_LOCATION,
    reference: str = "",
    note: str = "",
) -> StockMovement:
    """
    Writes one movement.

    The sign is derived from the reason rather than trusted from the caller,
    everywhere except `adjustment`. A caller that passed a positive number for a
    sale would otherwise silently double the stock of the thing it just sold, and
    that mistake is one line of application code away at every call site.
    """
    item = require_item(sku)
    amount = _quantity(quantity)

    if reason in INBOUND:
        amount = abs(amount)
    elif reason in OUTBOUND:
        amount = -abs(amount)
    elif reason != MovementReason.ADJUSTMENT:
        raise InventoryError(f"'{reason}' is not a movement reason this Feature knows.")

    return StockMovement.objects.create(
        item=item,
        location=location,
        quantity=amount,
        reason=reason,
        reference=reference,
        note=note,
    )


def receive(sku: str, quantity, *, location: str = DEFAULT_LOCATION, reference: str = "") -> StockMovement:
    """Stock arrived."""
    return record(sku, quantity, MovementReason.RECEIPT, location=location, reference=reference)


def sell(sku: str, quantity, *, location: str = DEFAULT_LOCATION, reference: str = "") -> StockMovement:
    """Stock left because somebody bought it."""
    return record(sku, quantity, MovementReason.SALE, location=location, reference=reference)


def take_back(sku: str, quantity, *, location: str = DEFAULT_LOCATION, reference: str = "") -> StockMovement:
    """
    Stock came back because somebody returned it.

    A new row rather than the removal of the sale row. The sale happened, and a
    ledger that edited it away would answer "how much did we sell in March" with
    a number that changes every time somebody returns something.
    """
    return record(sku, quantity, MovementReason.RETURN, location=location, reference=reference)


def write_off(sku: str, quantity, *, location: str = DEFAULT_LOCATION, note: str = "") -> StockMovement:
    """Stock left because it broke, spoiled or walked out of the door."""
    return record(sku, quantity, MovementReason.SHRINKAGE, location=location, note=note)


def count(sku: str, counted: Decimal | str | int, *, location: str = DEFAULT_LOCATION, note: str = "") -> StockMovement | None:
    """
    Records a stocktake: what somebody actually found on the shelf.

    Writes the *difference* rather than the total, which is what keeps this a
    ledger. A count that agrees with the books writes nothing at all and says so
    by returning None — the alternative is a movement of zero, which the database
    refuses and which would make "when did this last move" wrong anyway.
    """
    item = require_item(sku)
    found = _quantity(counted, allow_zero=True, allow_negative=False)
    difference = found - on_hand(item.sku, location=location)

    if difference == 0:
        return None

    return StockMovement.objects.create(
        item=item,
        location=location,
        quantity=difference,
        reason=MovementReason.ADJUSTMENT,
        note=note or f"Counted {found}",
    )


def transfer(sku: str, quantity, *, source: str, destination: str, reference: str = "") -> tuple[StockMovement, StockMovement]:
    """
    Moves stock between locations, as two rows in one transaction.

    Two rows because a transfer is two events and both locations have a history
    that has to make sense on its own. One transaction because a transfer that
    left one and never arrived at the other is stock that has ceased to exist.
    """
    if source == destination:
        raise InventoryError("A transfer needs two different locations.")

    amount = _quantity(quantity)

    with transaction.atomic():
        out = record(sku, amount, MovementReason.TRANSFER_OUT, location=source, reference=reference)
        into = record(sku, amount, MovementReason.TRANSFER_IN, location=destination, reference=reference)

    return out, into


# --- What there is ----------------------------------------------------------


def on_hand(sku: str, *, location: str | None = None) -> Decimal:
    """
    What is physically there: the sum of the ledger.

    Can be negative, and is reported that way. A shop whose books say -3 has a
    counting problem, and rounding it up to zero here would hide the one number
    that says so.
    """
    item = require_item(sku)
    movements = item.movements.all()

    if location is not None:
        movements = movements.filter(location=location)

    return _decimal(movements.aggregate(total=Sum("quantity"))["total"])


def held(sku: str, *, location: str | None = None, now=None) -> Decimal:
    """
    What is promised to somebody and not yet taken.

    Expired holds are excluded here as well as swept away hourly. The sweep is
    what keeps the table tidy; this is what keeps the arithmetic right in the
    hour before it runs — an expiry that only took effect when a job got round to
    it would mean stock unsellable for up to an hour after the basket died.
    """
    now = now or timezone.now()
    item = require_item(sku)

    reservations = item.reservations.filter(state=ReservationState.HELD, expires_at__gt=now)

    if location is not None:
        reservations = reservations.filter(location=location)

    return _decimal(reservations.aggregate(total=Sum("quantity"))["total"])


def available(sku: str, *, location: str | None = None, now=None) -> Decimal:
    """
    What may be sold: on hand, minus what is held, floored at zero.

    This is the number a storefront asks for. Everything else in this module
    exists so that this one is right.
    """
    return max(on_hand(sku, location=location) - held(sku, location=location, now=now), ZERO)


def level(sku: str, *, location: str = DEFAULT_LOCATION, now=None) -> Level:
    """All three numbers for one item, in one object."""
    item = require_item(sku)

    return Level(
        sku=item.sku,
        name=item.name,
        location=location,
        on_hand=on_hand(item.sku, location=location),
        held=held(item.sku, location=location, now=now),
        reorder_point=item.reorder_point,
    )


def levels(*, location: str = DEFAULT_LOCATION, tracked_only: bool = True, now=None) -> list[Level]:
    """
    Every item's numbers, in three queries rather than three per item.

    Written this way because the alternative is what a stock report usually is: a
    loop that runs two aggregates per row and takes a minute on a catalogue of
    two thousand.
    """
    now = now or timezone.now()
    items = StockItem.objects.all()

    if tracked_only:
        items = items.filter(is_tracked=True)

    items = list(items)
    identifiers = [item.pk for item in items]

    on_hand_by_item = {
        row["item"]: _decimal(row["total"])
        for row in StockMovement.objects.filter(item__in=identifiers, location=location)
        .values("item")
        .annotate(total=Sum("quantity"))
    }

    held_by_item = {
        row["item"]: _decimal(row["total"])
        for row in Reservation.objects.filter(
            item__in=identifiers,
            location=location,
            state=ReservationState.HELD,
            expires_at__gt=now,
        )
        .values("item")
        .annotate(total=Sum("quantity"))
    }

    return [
        Level(
            sku=item.sku,
            name=item.name,
            location=location,
            on_hand=on_hand_by_item.get(item.pk, ZERO),
            held=held_by_item.get(item.pk, ZERO),
            reorder_point=item.reorder_point,
        )
        for item in items
    ]


def history(sku: str, *, limit: int = 50) -> list[StockMovement]:
    """The recent movements of one item, newest first. The answer to 'why is this 7'."""
    item = require_item(sku)

    return list(item.movements.all()[: max(1, min(limit, 500))])


# --- Reservations -----------------------------------------------------------


@transaction.atomic
def reserve(
    sku: str,
    quantity,
    *,
    reference: str,
    location: str = DEFAULT_LOCATION,
    minutes: int | None = None,
    now=None,
) -> Reservation:
    """
    Promises stock to one basket or order.

    **The one function here that has to be correct under concurrency**, and the
    only one that takes a lock. Two shoppers reaching the last item at the same
    moment both read "1 available" and both write a hold, unless something
    serialises them — and there is no constraint that can express "the sum of
    these rows must not exceed the sum of those other rows".

    So it locks the item row with `select_for_update` first. Everything after
    that — the sum of the movements, the sum of the live holds, the comparison,
    the insert — happens with every other reserver for this item waiting. The
    lock is on the item rather than on a table so that reserving coffee never
    waits for somebody reserving cups.

    Idempotent on the reference. A checkout retried after a timeout finds its own
    hold and gets it back rather than doubling it, which the unique constraint
    would refuse anyway — this makes the good path a return instead of an error.
    """
    now = now or timezone.now()
    amount = _quantity(quantity)
    minutes = config.hold_minutes() if minutes is None else max(1, minutes)

    if amount <= 0:
        raise InventoryError("A reservation has to be for a positive quantity.")

    if not reference.strip():
        raise InventoryError("A reservation has to say what it is for.")

    # The lock. Nothing below this line races with another reserver of the same
    # item, and nothing above it needed to be protected.
    item = StockItem.objects.select_for_update().filter(sku=_sku(sku)).first()

    if item is None:
        raise UnknownItem(f"No stock item has the SKU '{sku}'.")

    existing = item.reservations.filter(reference=reference.strip()).first()

    if existing is not None:
        if existing.state == ReservationState.HELD and existing.expires_at > now:
            return existing

        # A settled reservation with this reference is not a hold to extend. It
        # is a caller reusing an order number for a second thing, which is a
        # mistake worth naming rather than absorbing.
        raise InventoryError(
            f"'{reference}' already has a {existing.state} reservation for {item.sku}."
        )

    free = max(_on_hand_locked(item, location) - _held_locked(item, location, now), ZERO)

    if free < amount:
        raise NotEnoughStock(item.sku, amount, free)

    return Reservation.objects.create(
        item=item,
        location=location,
        quantity=amount,
        reference=reference.strip(),
        expires_at=now + timedelta(minutes=minutes),
    )


@transaction.atomic
def commit(reference: str, *, now=None) -> list[StockMovement]:
    """
    Turns held stock into sold stock.

    This is where a movement is finally written: the reservation held the number
    and the sale moves it. Idempotent — a payment webhook delivered twice must
    not sell the same items twice — because the second call finds the
    reservations already committed and writes nothing.
    """
    now = now or timezone.now()
    reservations = list(
        Reservation.objects.select_for_update()
        .filter(reference=reference.strip(), state=ReservationState.HELD)
        .select_related("item")
    )

    movements = []

    for reservation in reservations:
        movements.append(
            StockMovement.objects.create(
                item=reservation.item,
                location=reservation.location,
                quantity=-reservation.quantity,
                reason=MovementReason.SALE,
                reference=reservation.reference,
            )
        )

        reservation.state = ReservationState.COMMITTED
        reservation.settled_at = now
        reservation.save(update_fields=["state", "settled_at"])

    return movements


@transaction.atomic
def release(reference: str, *, now=None) -> int:
    """
    Gives held stock back without selling it: an abandoned basket, a cancelled
    order, a payment that failed.

    Writes no movement, because nothing moved.
    """
    now = now or timezone.now()

    return _settle(
        Reservation.objects.filter(reference=reference.strip(), state=ReservationState.HELD),
        ReservationState.RELEASED,
        now,
    )


def expire_reservations(*, now=None) -> int:
    """
    Ends holds whose time is up. Declared as an hourly worker.

    Hourly rather than daily because the failure it prevents is measured in
    minutes of unsellable stock, and daily would mean a basket abandoned at 09:05
    keeping the last one of something off sale for the rest of the trading day.

    Safe to run twice, and safe to have not run: `held()` already excludes
    expired holds, so this is tidying rather than correctness. That order matters
    — an expiry that only took effect when the job ran would put the arithmetic
    at the mercy of a cron entry.
    """
    now = now or timezone.now()

    return _settle(
        Reservation.objects.filter(state=ReservationState.HELD, expires_at__lte=now),
        ReservationState.EXPIRED,
        now,
    )


# --- Alerts and reordering --------------------------------------------------


def sweep_low_stock(*, location: str = DEFAULT_LOCATION, now=None) -> dict[str, int]:
    """
    Raises alerts for what is low and resolves the ones that no longer are.
    Declared as a daily worker.

    Both halves matter equally. A sweep that only raised would leave a merchant
    reading a list of things they restocked last week, and a list nobody trusts
    is a list nobody reads.
    """
    now = now or timezone.now()
    raised = resolved = 0

    open_alerts = {
        alert.item_id: alert
        for alert in StockAlert.objects.filter(location=location, resolved_at__isnull=True)
    }

    # One lookup for the whole sweep. The version of this that resolved a SKU to
    # an id inside the loop ran a query per item, which is the shape a nightly
    # job takes when it gets slower every month until somebody notices.
    identifiers = dict(StockItem.objects.values_list("sku", "pk"))

    watch_everything = config.alerts_without_reorder_point()

    for current in levels(location=location, tracked_only=True, now=now):
        item_id = identifiers[current.sku]
        existing = open_alerts.pop(item_id, None)

        # An item whose reorder point is zero is one nobody has said "low" means
        # anything for. Alerting on it by default fills the list with things
        # that were never being watched, which is how a merchant learns to
        # ignore the list.
        if current.reorder_point <= 0 and not watch_everything:
            if existing is not None:
                existing.resolved_at = now
                existing.save(update_fields=["resolved_at"])
                resolved += 1
            continue

        if current.is_low:
            kind = AlertKind.OUT if current.available <= 0 else AlertKind.LOW

            if existing is None:
                StockAlert.objects.create(
                    item_id=item_id,
                    location=location,
                    kind=kind,
                    available=current.available,
                    threshold=current.reorder_point,
                )
                raised += 1
            elif existing.kind != kind:
                # Low yesterday and out today is worse news, and a merchant
                # scanning the list has to see the change. The old alert is
                # resolved and a new one raised rather than edited in place: an
                # alert is a statement about a moment.
                existing.resolved_at = now
                existing.save(update_fields=["resolved_at"])

                StockAlert.objects.create(
                    item_id=item_id,
                    location=location,
                    kind=kind,
                    available=current.available,
                    threshold=current.reorder_point,
                )
                raised += 1
        elif existing is not None:
            existing.resolved_at = now
            existing.save(update_fields=["resolved_at"])
            resolved += 1

    # Anything left is an alert for an item that is no longer tracked at all.
    for orphan in open_alerts.values():
        orphan.resolved_at = now
        orphan.save(update_fields=["resolved_at"])
        resolved += 1

    return {"raised": raised, "resolved": resolved}


def open_alerts(*, location: str | None = None) -> list[StockAlert]:
    """What is low right now."""
    alerts = StockAlert.objects.filter(resolved_at__isnull=True).select_related("item")

    if location is not None:
        alerts = alerts.filter(location=location)

    return list(alerts)


def reorder_suggestions(*, location: str = DEFAULT_LOCATION, now=None) -> list[Suggestion]:
    """
    What to buy, and what is already on its way.

    `on_order` is the part that makes this usable. A reorder list that ignored
    outstanding purchase orders would suggest ordering the same thing every
    morning until the delivery arrived, and a merchant following it would end up
    with five deliveries of it.
    """
    now = now or timezone.now()

    outstanding: dict[int, Decimal] = {}
    lines = PurchaseOrderLine.objects.filter(
        order__state__in=(PurchaseOrderState.PLACED, PurchaseOrderState.PARTIAL)
    ).values("item").annotate(ordered=Sum("quantity_ordered"), received=Sum("quantity_received"))

    for row in lines:
        outstanding[row["item"]] = _decimal(row["ordered"]) - _decimal(row["received"])

    by_sku = {item.sku: item for item in StockItem.objects.select_related("supplier")}
    suggestions = []

    for current in levels(location=location, tracked_only=True, now=now):
        if not current.is_low:
            continue

        item = by_sku[current.sku]

        suggestions.append(
            Suggestion(
                sku=item.sku,
                name=item.name,
                available=current.available,
                reorder_point=item.reorder_point,
                suggested_quantity=item.reorder_quantity,
                supplier_code=item.supplier.code if item.supplier else None,
                on_order=outstanding.get(item.pk, ZERO),
            )
        )

    return suggestions


# --- Purchase orders --------------------------------------------------------


def create_purchase_order(
    reference: str,
    *,
    supplier_code: str,
    location: str = DEFAULT_LOCATION,
    expected_at=None,
    notes: str = "",
) -> PurchaseOrder:
    """Opens a draft order. Nothing is expected to arrive until it is placed."""
    supplier = Supplier.objects.filter(code=supplier_code).first()

    if supplier is None:
        raise InventoryError(f"No supplier has the code '{supplier_code}'.")

    if not reference.strip():
        raise InventoryError("A purchase order needs a reference.")

    if PurchaseOrder.objects.filter(reference=reference.strip()).exists():
        raise InventoryError(f"A purchase order with the reference '{reference}' already exists.")

    return PurchaseOrder.objects.create(
        reference=reference.strip(),
        supplier=supplier,
        location=location,
        expected_at=expected_at,
        notes=notes,
    )


def add_line(reference: str, sku: str, quantity, *, unit_cost: Decimal | str | int = 0) -> PurchaseOrderLine:
    """
    Adds an item to a draft order.

    Only to a draft. Changing what was ordered after the order was sent means the
    document in the supplier's hands and the document in the shop disagree, and
    the receiving clerk is the one who finds out.
    """
    order = _order(reference)

    if order.state != PurchaseOrderState.DRAFT:
        raise InventoryError(f"{order.reference} is {order.state}; only a draft order can be changed.")

    item = require_item(sku)

    line, created = PurchaseOrderLine.objects.get_or_create(
        order=order,
        item=item,
        defaults={"quantity_ordered": _quantity(quantity), "unit_cost": Decimal(str(unit_cost))},
    )

    if not created:
        line.quantity_ordered = _quantity(quantity)
        line.unit_cost = Decimal(str(unit_cost))
        line.save(update_fields=["quantity_ordered", "unit_cost"])

    return line


def place(reference: str, *, now=None) -> PurchaseOrder:
    """Sends the order. An order with no lines is refused rather than sent empty."""
    order = _order(reference)

    if order.state != PurchaseOrderState.DRAFT:
        raise InventoryError(f"{order.reference} has already been {order.state}.")

    if not order.lines.exists():
        raise InventoryError(f"{order.reference} has no lines; there is nothing to order.")

    order.state = PurchaseOrderState.PLACED
    order.placed_at = now or timezone.now()
    order.save(update_fields=["state", "placed_at"])

    return order


@transaction.atomic
def receive_line(reference: str, sku: str, quantity) -> StockMovement:
    """
    Records part or all of a line arriving, and moves the stock in the same
    transaction.

    The two are one act. Receiving stock that was recorded against the order but
    never added to the shelf is a shop whose books say it has coffee it cannot
    find; the reverse is a shop that reorders something it already has.

    Over-receiving is refused. A delivery larger than the order is either
    somebody else's stock or a typo, and both are worth stopping to look at.
    """
    order = _order(reference)

    if order.state not in (PurchaseOrderState.PLACED, PurchaseOrderState.PARTIAL):
        raise InventoryError(f"{order.reference} is {order.state}; nothing can be received against it.")

    item = require_item(sku)
    line = order.lines.select_for_update().filter(item=item).first()

    if line is None:
        raise InventoryError(f"{order.reference} has no line for {item.sku}.")

    amount = _quantity(quantity)

    if line.quantity_received + amount > line.quantity_ordered:
        raise InventoryError(
            f"{order.reference} ordered {line.quantity_ordered} of {item.sku} and "
            f"{line.quantity_received} has already arrived; {amount} more would be too much."
        )

    line.quantity_received += amount
    line.save(update_fields=["quantity_received"])

    movement = StockMovement.objects.create(
        item=item,
        location=order.location,
        quantity=amount,
        reason=MovementReason.RECEIPT,
        reference=order.reference,
    )

    _restate(order)

    return movement


def cancel_purchase_order(reference: str) -> PurchaseOrder:
    """
    Cancels an order.

    Refused once anything has arrived against it: those movements are real, and
    an order marked cancelled with stock received under its reference is a
    history that cannot be read.
    """
    order = _order(reference)

    if order.state == PurchaseOrderState.RECEIVED:
        raise InventoryError(f"{order.reference} has been received in full and cannot be cancelled.")

    if order.lines.filter(quantity_received__gt=0).exists():
        raise InventoryError(
            f"{order.reference} has stock received against it. Receive the rest or leave it open; "
            "cancelling it would orphan movements that really happened."
        )

    order.state = PurchaseOrderState.CANCELLED
    order.save(update_fields=["state"])

    return order


def outstanding_orders() -> list[PurchaseOrder]:
    """Orders that have been placed and have not fully arrived."""
    return list(
        PurchaseOrder.objects.filter(
            state__in=(PurchaseOrderState.PLACED, PurchaseOrderState.PARTIAL)
        ).select_related("supplier")
    )


# --- Workers ----------------------------------------------------------------


def run_expiry() -> dict[str, int]:
    """The hourly worker's entrypoint. No arguments, by contract."""
    return {"expired": expire_reservations()}


def run_low_stock_sweep() -> dict[str, int]:
    """The daily worker's entrypoint. No arguments, by contract."""
    return sweep_low_stock()


# --- Internals --------------------------------------------------------------


def require_item(sku: str) -> StockItem:
    item = StockItem.objects.filter(sku=_sku(sku)).first()

    if item is None:
        raise UnknownItem(f"No stock item has the SKU '{sku}'.")

    return item


def _order(reference: str) -> PurchaseOrder:
    order = PurchaseOrder.objects.filter(reference=(reference or "").strip()).first()

    if order is None:
        raise InventoryError(f"No purchase order has the reference '{reference}'.")

    return order


def _restate(order: PurchaseOrder) -> None:
    """
    Sets the order's state from its lines.

    Derived rather than set by the caller: an order is received because every
    line is, and a state somebody typed can disagree with the lines underneath
    it.
    """
    lines = list(order.lines.all())
    state = PurchaseOrderState.PLACED

    if lines and all(line.quantity_received >= line.quantity_ordered for line in lines):
        state = PurchaseOrderState.RECEIVED
    elif any(line.quantity_received > 0 for line in lines):
        state = PurchaseOrderState.PARTIAL

    if state != order.state:
        order.state = state
        order.save(update_fields=["state"])


def _settle(reservations: QuerySet, state: str, now) -> int:
    """
    Closes a set of holds in one statement.

    An update rather than a loop: expiry runs against everything a store has
    abandoned since the last sweep, and a row-at-a-time version of this is the
    kind of job that gets slower every month until somebody notices.
    """
    return reservations.update(state=state, settled_at=now)


def _on_hand_locked(item: StockItem, location: str) -> Decimal:
    return _decimal(
        StockMovement.objects.filter(item=item, location=location)
        .aggregate(total=Sum("quantity"))["total"]
    )


def _held_locked(item: StockItem, location: str, now) -> Decimal:
    return _decimal(
        Reservation.objects.filter(
            item=item, location=location, state=ReservationState.HELD, expires_at__gt=now
        ).aggregate(total=Sum("quantity"))["total"]
    )


def _sku(value: str) -> str:
    """
    SKUs compare case-insensitively and without surrounding space.

    Normalised on the way in rather than matched case-insensitively on the way
    out, so that the uniqueness is the database's and not this module's. The
    store's own catalogue does the same thing with `normalized_sku`, and a
    delivery clerk typing `esp-01` must reach the item somebody created as
    `ESP-01`.
    """
    return (value or "").strip().upper()


def _quantity(value, *, allow_zero: bool = False, allow_negative: bool = True) -> Decimal:
    try:
        amount = Decimal(str(value)).quantize(ZERO)
    except (ArithmeticError, ValueError) as exc:
        raise InventoryError(f"'{value}' is not a quantity.") from exc

    if not allow_zero and amount == 0:
        raise InventoryError("A quantity of zero is not a movement.")

    if not allow_negative and amount < 0:
        raise InventoryError("This quantity cannot be negative.")

    return amount


def _decimal(value) -> Decimal:
    return Decimal(value or 0).quantize(ZERO)
