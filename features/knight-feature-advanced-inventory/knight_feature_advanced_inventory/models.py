"""
The inventory tables.

One rule decides the shape of all of them, and it is the rule the two ledgers in
phase 14 were built on: **the movements are the truth and the quantity is
derived**. There is no `quantity` column on a stock item anywhere in this
package. What a shop has is the sum of what arrived minus what left, and every
one of those events is a row saying when it happened, why, and what it was for.

The alternative — a counter each operation increments — is what most inventory
bugs are made of. Two sales at once and the counter is wrong by one; a refund
applied twice and it is wrong by two; and the day somebody asks *why* the number
is 7 there is no answer, because the history was never written down. A ledger
cannot drift from itself, and "why is this 7" is a query.

The cost is real and accepted deliberately: reading a stock level sums rows
rather than reading a column. It is indexed for exactly that query, and if a
catalogue ever outgrows it the answer is a periodic snapshot that the ledger is
still the truth behind — never a mutable counter.

A second decision that cannot be taken later: quantities are decimals to three
places, not integers. A shop selling coffee by the 250g bag counts in units and
a shop selling it by weight does not, and widening an integer column of stock
quantities after a year of trading is a migration nobody wants to write.
"""

from django.contrib.postgres.indexes import GinIndex
from django.core.validators import MinValueValidator
from django.db import models

#: The location a movement belongs to when a store has only one. An empty string
#: rather than null: it takes part in a unique constraint and in every grouping
#: here, and NULL does not compare equal to itself.
#:
#: The column exists in 1.0 although nothing yet sets it, and that is deliberate.
#: `multi-location` gives these codes names, staff and routing, and it cannot add
#: the column itself — a Feature owns only its own tables. Adding it afterwards
#: would be a migration over every movement a store had ever recorded.
DEFAULT_LOCATION = ""

#: Three places, for the reason in the module docstring.
QUANTITY = {"max_digits": 14, "decimal_places": 3}


class MovementReason(models.TextChoices):
    """
    Why stock moved. Every row has one, and the list is closed.

    Free text here would make the ledger unreadable within a month: the same
    event spelled four ways is four events as far as any report is concerned.
    """

    RECEIPT = "receipt", "Received from a supplier"
    SALE = "sale", "Sold to a customer"
    RETURN = "return", "Returned by a customer"
    ADJUSTMENT = "adjustment", "Counted and corrected"
    SHRINKAGE = "shrinkage", "Lost, damaged or written off"
    TRANSFER_IN = "transfer-in", "Moved in from another location"
    TRANSFER_OUT = "transfer-out", "Moved out to another location"


class Supplier(models.Model):
    """Who stock is bought from."""

    code = models.CharField(max_length=40, unique=True)
    name = models.CharField(max_length=200)
    email = models.EmailField(blank=True, default="")
    phone = models.CharField(max_length=40, blank=True, default="")

    #: How long an order from this supplier usually takes, so that a reorder can
    #: be called late rather than merely outstanding.
    lead_time_days = models.PositiveSmallIntegerField(default=7)

    is_active = models.BooleanField(default=True)
    notes = models.TextField(blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_inventory_supplier"
        ordering = ("name",)
        indexes = [
            # Trigrams over the name, so a half-remembered supplier is findable.
            # A GIN index rather than a scan, because a supplier picker is typed
            # into and re-queried on every keystroke.
            GinIndex(fields=["name"], name="knight_inv_supplier_trgm", opclasses=["gin_trgm_ops"]),
        ]

    def __str__(self) -> str:
        return f"{self.code} {self.name}"


class StockItem(models.Model):
    """
    One thing whose stock is tracked.

    Keyed by SKU, which is the store's own identifier for a sellable thing, and
    carrying `object_id` as a plain integer with no foreign key — the arrangement
    `advanced-search` uses, for the same reason: a Feature may not reference a
    store's tables. The consequence is stated rather than hidden. An item whose
    product was deleted keeps its history here until somebody removes it, which
    is right for a ledger: the stock really did move.
    """

    sku = models.CharField(max_length=100, unique=True)
    name = models.CharField(max_length=200)

    #: The store's row id for this thing, when it has one. Nullable because a
    #: shop tracks stock of things it does not sell directly — cups, packaging,
    #: the beans it grinds into the drinks on the menu.
    object_id = models.BigIntegerField(null=True, blank=True, db_index=True)

    #: What one of these is. Free text and deliberately so: "kg", "bag", "bottle"
    #: and "each" are all correct and none of them is KNIGHT's business.
    unit = models.CharField(max_length=20, default="each")

    supplier = models.ForeignKey(
        Supplier,
        on_delete=models.SET_NULL,
        null=True,
        blank=True,
        related_name="items",
    )

    #: Below this, the daily sweep raises an alert and the item joins the reorder
    #: list.
    reorder_point = models.DecimalField(**QUANTITY, default=0, validators=[MinValueValidator(0)])

    #: How much to order when it does. Zero means nobody has decided, and the
    #: suggestion says so rather than inventing a number.
    reorder_quantity = models.DecimalField(**QUANTITY, default=0, validators=[MinValueValidator(0)])

    #: An item stocked but not counted — a service, or a dish whose components
    #: are tracked instead. It keeps its row and stays out of alerts and reorder
    #: suggestions.
    is_tracked = models.BooleanField(default=True)

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        db_table = "knight_inventory_item"
        ordering = ("sku",)
        indexes = [
            models.Index(fields=["is_tracked"], name="knight_inv_item_tracked"),
            GinIndex(fields=["sku"], name="knight_inv_item_sku_trgm", opclasses=["gin_trgm_ops"]),
            GinIndex(fields=["name"], name="knight_inv_item_name_trgm", opclasses=["gin_trgm_ops"]),
        ]

    def __str__(self) -> str:
        return f"{self.sku} ({self.name})"


class StockMovement(models.Model):
    """
    One thing that happened to the stock of one item.

    **Append-only.** Nothing in this package updates or deletes a movement, and a
    correction is another movement rather than an edit — which is what makes "why
    is this 7" answerable a year later. A refund writes a return row; it does not
    undo the sale row, because the sale did happen.
    """

    item = models.ForeignKey(StockItem, on_delete=models.CASCADE, related_name="movements")
    location = models.CharField(max_length=40, blank=True, default=DEFAULT_LOCATION)

    #: Signed. Positive brought stock in, negative took it out. `services` is
    #: what enforces that a sale is negative and a receipt positive, because that
    #: is a rule about reasons rather than about rows.
    quantity = models.DecimalField(**QUANTITY)

    reason = models.CharField(max_length=20, choices=MovementReason)

    #: What this movement was for: an order number, a purchase-order reference, a
    #: stocktake id. Free text, indexed, and the only way to answer "what
    #: happened to order 4471".
    reference = models.CharField(max_length=100, blank=True, default="")

    note = models.CharField(max_length=300, blank=True, default="")
    occurred_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_inventory_movement"
        ordering = ("-occurred_at", "-id")
        indexes = [
            # The index that makes a derived quantity affordable: every stock
            # level query in this package groups by exactly this pair.
            models.Index(fields=["item", "location"], name="knight_inv_move_item_loc"),
            models.Index(fields=["reference"], name="knight_inv_move_reference"),
            models.Index(fields=["occurred_at"], name="knight_inv_move_occurred"),
        ]
        constraints = [
            # A movement of nothing is not an event. It would sit in the history
            # implying something happened and would make every "when did this
            # last move" answer wrong.
            models.CheckConstraint(
                condition=~models.Q(quantity=0),
                name="knight_inv_movement_is_not_zero",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.item_id} {self.quantity:+} ({self.reason})"


class ReservationState(models.TextChoices):
    HELD = "held", "Held"
    COMMITTED = "committed", "Committed"
    RELEASED = "released", "Released"
    EXPIRED = "expired", "Expired"


class Reservation(models.Model):
    """
    Stock promised to somebody who has not taken it yet.

    The thing that makes a checkout honest: between "add to basket" and "payment
    taken", the last one of something must not be sellable twice. A reservation
    holds it without moving it — no movement row is written until the sale
    happens, because nothing has left the shelf.

    Every reservation expires. A hold that could not would be the worst failure
    this Feature has: an abandoned basket permanently removing the only one of
    something from sale, in a way that looks exactly like being out of stock.
    """

    item = models.ForeignKey(StockItem, on_delete=models.CASCADE, related_name="reservations")
    location = models.CharField(max_length=40, blank=True, default=DEFAULT_LOCATION)
    quantity = models.DecimalField(**QUANTITY, validators=[MinValueValidator(0)])

    #: The basket or order this hold is for. Unique per item, which makes
    #: reserving twice for one order impossible rather than merely unlikely: a
    #: retried checkout must not hold the stock twice.
    reference = models.CharField(max_length=100)

    state = models.CharField(max_length=12, choices=ReservationState, default=ReservationState.HELD)
    expires_at = models.DateTimeField()
    created_at = models.DateTimeField(auto_now_add=True)
    settled_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        db_table = "knight_inventory_reservation"
        ordering = ("-created_at",)
        indexes = [
            models.Index(fields=["item", "location", "state"], name="knight_inv_res_item_state"),
            models.Index(fields=["state", "expires_at"], name="knight_inv_res_expiry"),
        ]
        constraints = [
            models.UniqueConstraint(
                fields=["item", "reference"],
                name="knight_inv_one_reservation_per_reference",
            ),
            models.CheckConstraint(
                condition=models.Q(quantity__gt=0),
                name="knight_inv_reservation_is_positive",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.quantity} of {self.item_id} for {self.reference} ({self.state})"


class AlertKind(models.TextChoices):
    LOW = "low-stock", "Low stock"
    OUT = "out-of-stock", "Out of stock"


class StockAlert(models.Model):
    """
    A raised, unresolved warning that an item needs attention.

    A record rather than a notification, because what a merchant needs is "what
    is low right now", not an inbox. One unresolved alert per item and location
    is a database constraint: a daily sweep raising a fresh alert every morning
    would bury the item that has been out for a week under thirty copies of
    itself.
    """

    item = models.ForeignKey(StockItem, on_delete=models.CASCADE, related_name="alerts")
    location = models.CharField(max_length=40, blank=True, default=DEFAULT_LOCATION)
    kind = models.CharField(max_length=16, choices=AlertKind)

    #: What the numbers were when this was raised. Kept rather than recomputed on
    #: read: an alert is a statement about a moment, and one that silently
    #: restated itself with today's figures would be unreadable after the fact.
    available = models.DecimalField(**QUANTITY)
    threshold = models.DecimalField(**QUANTITY)

    raised_at = models.DateTimeField(auto_now_add=True)
    resolved_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        db_table = "knight_inventory_alert"
        ordering = ("-raised_at",)
        constraints = [
            models.UniqueConstraint(
                fields=["item", "location"],
                condition=models.Q(resolved_at__isnull=True),
                name="knight_inv_one_open_alert_per_item",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.kind} for {self.item_id}"


class PurchaseOrderState(models.TextChoices):
    DRAFT = "draft", "Draft"
    PLACED = "placed", "Placed"
    PARTIAL = "partially-received", "Partially received"
    RECEIVED = "received", "Received"
    CANCELLED = "cancelled", "Cancelled"


class PurchaseOrder(models.Model):
    """
    Stock ordered from a supplier and not yet in the building.

    Its state is derived from its lines wherever it can be — an order is
    `received` because every line is, not because somebody said so — with the two
    exceptions that are genuinely decisions rather than facts: placing it and
    cancelling it.
    """

    reference = models.CharField(max_length=60, unique=True)
    supplier = models.ForeignKey(Supplier, on_delete=models.PROTECT, related_name="orders")
    state = models.CharField(max_length=20, choices=PurchaseOrderState, default=PurchaseOrderState.DRAFT)
    location = models.CharField(max_length=40, blank=True, default=DEFAULT_LOCATION)

    expected_at = models.DateField(null=True, blank=True)
    placed_at = models.DateTimeField(null=True, blank=True)
    notes = models.TextField(blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_inventory_purchase_order"
        ordering = ("-created_at",)
        indexes = [
            models.Index(fields=["state"], name="knight_inv_po_state"),
            models.Index(fields=["supplier", "state"], name="knight_inv_po_supplier"),
        ]

    def __str__(self) -> str:
        return f"{self.reference} ({self.state})"


class PurchaseOrderLine(models.Model):
    """One item on a purchase order, and how much of it has actually arrived."""

    order = models.ForeignKey(PurchaseOrder, on_delete=models.CASCADE, related_name="lines")
    item = models.ForeignKey(StockItem, on_delete=models.PROTECT, related_name="purchase_lines")

    quantity_ordered = models.DecimalField(**QUANTITY, validators=[MinValueValidator(0)])

    #: Never edited downwards: a short delivery is recorded by receiving less,
    #: not by pretending less was ordered.
    quantity_received = models.DecimalField(**QUANTITY, default=0, validators=[MinValueValidator(0)])

    unit_cost = models.DecimalField(max_digits=12, decimal_places=2, default=0)

    class Meta:
        db_table = "knight_inventory_purchase_line"
        ordering = ("id",)
        constraints = [
            models.UniqueConstraint(
                fields=["order", "item"],
                name="knight_inv_one_line_per_item_per_order",
            ),
            models.CheckConstraint(
                condition=models.Q(quantity_ordered__gt=0),
                name="knight_inv_po_line_is_positive",
            ),
        ]

    @property
    def outstanding(self):
        return self.quantity_ordered - self.quantity_received

    def __str__(self) -> str:
        return f"{self.quantity_ordered} of {self.item_id} on {self.order_id}"
