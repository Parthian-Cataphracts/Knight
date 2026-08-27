"""
The restaurant tables.

Three decisions shape all of them, and each of them is a mistake this Feature
exists not to make.

**The kitchen's clock is not the shopper's clock.** A store's `Order` already has
a status, and it is the right one for the person who is waiting: confirmed,
preparing, ready. A kitchen needs finer states than that, per line and not only
per order — the burger is on the grill while the salad has not been started — and
it needs them for tickets that were never orders at all, like a table that
ordered at the counter. So a ticket is its own record with its own states, and it
carries the order's *number* rather than a foreign key to it. The Feature never
writes to a store's order, and a store that uninstalls this keeps every order it
ever took.

**A promise is a maximum, not a sum.** A kitchen cooks in parallel. Adding four
ten-minute dishes together and quoting forty minutes is how a shop turns a good
kitchen into a bad reputation in both directions: it is wrong when it is quoted,
and it is wrong again when the food arrives half an hour early and cold service
gets blamed for it. What a ticket takes is the longest thing on it, plus what the
kitchen is already carrying.

**Capacity is a promise, not a display.** A pickup time that is shown but not
taken is how two people are told 18:30 for the same last slot. Slots are booked,
bookings are rows, and what is left in a slot is derived from them — the same
shape as `advanced-inventory`'s reservations and for the same reason: there is no
counter to drift, and no constraint that can express "the sum of these rows must
not exceed that number", which is why booking is the one path here that locks.

The `location` column on the tickets, the tables and the slots is the column
`advanced-inventory` carries for the same reason. It is a bare code that nothing
in this package gives meaning to; `multi-location` is what names it. It exists in
1.0 because a Feature owns only its own tables, and adding it afterwards would be
a migration over every ticket a restaurant had ever printed.
"""

from django.core.validators import MinValueValidator
from django.db import models

#: The location a row belongs to when a restaurant has only one. An empty string
#: rather than null: it takes part in unique constraints and in grouping here,
#: and NULL does not compare equal to itself.
DEFAULT_LOCATION = ""


class ServiceStyle(models.TextChoices):
    """
    How the food gets to whoever is eating it.

    It changes what a promised time means, which is why it is on the ticket
    rather than inferred. A dine-in ticket is promised to a table that is already
    sitting there; a collection ticket is promised to somebody who has to travel.
    """

    DINE_IN = "dine-in", "Eaten here"
    COLLECTION = "collection", "Collected"
    DELIVERY = "delivery", "Delivered"


class ServiceArea(models.Model):
    """
    A named part of the room: the terrace, upstairs, the bar.

    Its own table rather than a string on the table, because a restaurant closes
    the terrace when it rains and needs one switch to do it.
    """

    code = models.CharField(max_length=40, unique=True)
    name = models.CharField(max_length=120)
    location = models.CharField(max_length=40, blank=True, default=DEFAULT_LOCATION)
    is_open = models.BooleanField(default=True)
    display_order = models.PositiveSmallIntegerField(default=0)

    class Meta:
        db_table = "knight_restaurant_area"
        ordering = ("display_order", "code")

    def __str__(self) -> str:
        return self.name or self.code


class Table(models.Model):
    """
    One table, as staff refer to it.

    `code` is what somebody shouts across the room — "twelve", "T4" — and it is
    the identity, not the row id. Merging two tables for a large party is not
    modelled: restaurants do it by seating the party at one and noting the other
    is in use, and a join-table for something staff resolve in five seconds would
    be a schema that lies whenever they do it differently.
    """

    code = models.CharField(max_length=20, unique=True)
    name = models.CharField(max_length=120, blank=True, default="")
    area = models.ForeignKey(
        ServiceArea,
        on_delete=models.SET_NULL,
        null=True,
        blank=True,
        related_name="tables",
    )
    location = models.CharField(max_length=40, blank=True, default=DEFAULT_LOCATION)
    seats = models.PositiveSmallIntegerField(default=2)

    #: A table out of service — broken leg, being repainted — keeps its history
    #: and stops being offered. Deleting it would take its tickets with it.
    is_active = models.BooleanField(default=True)
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_restaurant_table"
        ordering = ("code",)
        indexes = [
            models.Index(fields=["location", "is_active"], name="knight_rest_table_active"),
        ]
        constraints = [
            models.CheckConstraint(
                condition=models.Q(seats__gt=0),
                name="knight_rest_table_has_seats",
            ),
        ]

    def __str__(self) -> str:
        return self.name or self.code


class TableSession(models.Model):
    """
    A party at a table, from sitting down to paying.

    One open session per table is a database constraint rather than a check in
    the code, because the failure it prevents is two parties' food arriving on
    one bill. It is a partial unique index — unique among the rows that have not
    closed — which is the only way to say "one at a time" about something that
    happens repeatedly at the same table all evening.
    """

    table = models.ForeignKey(Table, on_delete=models.PROTECT, related_name="sessions")
    party_size = models.PositiveSmallIntegerField(default=1)

    #: What staff call this party. Free text, because "the two by the window" is
    #: how a restaurant actually identifies a table it has not taken a name for.
    label = models.CharField(max_length=120, blank=True, default="")

    opened_at = models.DateTimeField(auto_now_add=True)
    closed_at = models.DateTimeField(null=True, blank=True)

    #: Why it closed. `''` while it is open, and one of the closure reasons after
    #: — a session the nightly sweep closed is a different fact from one somebody
    #: closed by taking payment, and a report that could not tell them apart
    #: would count abandoned tables as covers.
    closed_reason = models.CharField(max_length=40, blank=True, default="")

    class Meta:
        db_table = "knight_restaurant_session"
        ordering = ("-opened_at",)
        indexes = [
            models.Index(fields=["table", "closed_at"], name="knight_rest_session_open"),
        ]
        constraints = [
            models.UniqueConstraint(
                fields=["table"],
                condition=models.Q(closed_at__isnull=True),
                name="knight_rest_one_open_session_per_table",
            ),
        ]

    @property
    def is_open(self) -> bool:
        return self.closed_at is None

    def __str__(self) -> str:
        return f"{self.table_id} ({self.label or self.party_size})"


class Station(models.Model):
    """
    A place in the kitchen with its own queue: grill, cold, bar, pass.

    Lines are routed to stations, and a station is the unit the display screens
    are filtered by — the grill chef does not want to read the drinks. It carries
    its own throughput because stations are not equally fast, and a promise that
    assumed they were would be wrong for whichever one is the bottleneck.
    """

    code = models.CharField(max_length=40, unique=True)
    name = models.CharField(max_length=120)
    location = models.CharField(max_length=40, blank=True, default=DEFAULT_LOCATION)

    #: How much work this station can clear in an hour, in the same load units
    #: the prep profiles are measured in. It is what turns a backlog into
    #: minutes.
    throughput_units_per_hour = models.PositiveIntegerField(default=60)

    is_active = models.BooleanField(default=True)
    display_order = models.PositiveSmallIntegerField(default=0)

    class Meta:
        db_table = "knight_restaurant_station"
        ordering = ("display_order", "code")
        constraints = [
            models.CheckConstraint(
                condition=models.Q(throughput_units_per_hour__gt=0),
                name="knight_rest_station_has_throughput",
            ),
        ]

    def __str__(self) -> str:
        return self.name or self.code


class PrepProfile(models.Model):
    """
    How long one thing takes to make, and how much of the kitchen it occupies
    while it does.

    Keyed by SKU, and holding `object_id` as a plain integer with no foreign key
    — the arrangement `advanced-inventory` and `advanced-search` both use, for
    the same reason: a Feature may not reference a store's tables.

    Two numbers rather than one, and the second is the one restaurants forget.
    Minutes are how long the customer waits. Load units are how much of the
    kitchen the dish uses up, and they are not proportional: a plate of olives
    takes two minutes and no attention, a soufflé takes twenty and the whole
    oven. Throttling on minutes alone would let a kitchen accept twelve soufflés
    for the same slot.
    """

    sku = models.CharField(max_length=100, unique=True)
    name = models.CharField(max_length=200)
    object_id = models.BigIntegerField(null=True, blank=True, db_index=True)

    station = models.ForeignKey(
        Station,
        on_delete=models.SET_NULL,
        null=True,
        blank=True,
        related_name="items",
    )

    prep_minutes = models.PositiveSmallIntegerField(default=10)
    load_units = models.PositiveSmallIntegerField(default=1)

    #: Something the kitchen does not make: a bottle of wine, a bag of crisps. It
    #: keeps a profile so a ticket can still show it to whoever is carrying the
    #: tray, and contributes nothing to the promise or to the load.
    is_prepared = models.BooleanField(default=True)

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        db_table = "knight_restaurant_prep_profile"
        ordering = ("sku",)

    def __str__(self) -> str:
        return f"{self.sku} ({self.prep_minutes}m)"


class TicketState(models.TextChoices):
    """
    Where a ticket is, from the kitchen's point of view.

    Finer than an order's status and deliberately so. `held` exists because a
    ticket for 19:30 printed at 18:00 must not sit on the grill screen for ninety
    minutes; `on-hold` after starting does not exist, because a kitchen that
    stops cooking something halfway through has a conversation, not a state.
    """

    SCHEDULED = "scheduled", "Scheduled for later"
    QUEUED = "queued", "Waiting to be started"
    PREPARING = "preparing", "Being made"
    READY = "ready", "Ready at the pass"
    SERVED = "served", "Handed over"
    CANCELLED = "cancelled", "Cancelled"


#: What each ticket state may become. Read by the aggregate rather than scattered
#: through it, so the whole rule is visible in one place and a new state cannot
#: be added without deciding what it follows. The same arrangement the store's
#: own order aggregate uses.
#:
#: `ready` may go back to `preparing`: a dish sent back is the single most common
#: thing that happens at a pass, and a workflow with no way to express it is one
#: staff work around by opening a new ticket, which loses the connection to the
#: order that is actually wrong.
ALLOWED_TRANSITIONS: dict[str, set[str]] = {
    TicketState.SCHEDULED: {TicketState.QUEUED, TicketState.CANCELLED},
    TicketState.QUEUED: {TicketState.PREPARING, TicketState.CANCELLED},
    TicketState.PREPARING: {TicketState.READY, TicketState.CANCELLED},
    TicketState.READY: {TicketState.SERVED, TicketState.PREPARING, TicketState.CANCELLED},
    TicketState.SERVED: set(),
    TicketState.CANCELLED: set(),
}


class TicketNumberSequence(models.Model):
    """
    The counter behind the short numbers the kitchen shouts.

    A single row, and the same shape as the store's own order-number sequence,
    for the same reason: two tickets opened in the same second would both count
    the same number of predecessors and take the same number, and a ticket number
    is what somebody calls out when the food is at the pass.

    It rolls over rather than growing forever. A kitchen with a four-digit ticket
    number has stopped using it as a shorthand, which is the only thing it is
    for; uniqueness across all of history is `source_order_number`'s job.
    """

    id = models.PositiveSmallIntegerField(primary_key=True, default=1)
    last_value = models.BigIntegerField(default=0)

    class Meta:
        db_table = "knight_restaurant_ticket_sequence"

    @classmethod
    def take(cls, *, wrap_at: int = 9999) -> int:
        """
        Reserves the next number.

        `select_for_update` rather than an increment-and-read, so two tills
        opening tickets at once are serialised by the row lock instead of racing.
        Callers are already inside a transaction; this asserts nothing and relies
        on that, because a number handed out and rolled back is a gap staff will
        ask about.
        """
        counter = cls.objects.select_for_update().get_or_create(id=1)[0]
        counter.last_value = (counter.last_value % wrap_at) + 1
        counter.save(update_fields=["last_value"])

        return counter.last_value


class KitchenTicket(models.Model):
    """
    One thing the kitchen has to make, for one order or one table.

    `source_order_number` is the store's own order number kept as a plain
    integer, never a foreign key. It is what staff read out and what a member of
    staff types into a search box, and it stays readable after the order it came
    from is archived. Nullable, because a table that ordered at the counter is a
    ticket with no order behind it yet.
    """

    source_order_number = models.BigIntegerField(null=True, blank=True, db_index=True)

    #: What the kitchen calls this ticket. Short, sequential within a service and
    #: assigned by this Feature, because an order number seven digits long is not
    #: something anybody shouts across a kitchen.
    number = models.PositiveIntegerField(unique=True, editable=False)

    service = models.CharField(max_length=20, choices=ServiceStyle, default=ServiceStyle.DINE_IN)
    state = models.CharField(max_length=20, choices=TicketState, default=TicketState.QUEUED)
    location = models.CharField(max_length=40, blank=True, default=DEFAULT_LOCATION)

    session = models.ForeignKey(
        TableSession,
        on_delete=models.SET_NULL,
        null=True,
        blank=True,
        related_name="tickets",
    )

    #: When the kitchen said it would be done. Stored rather than recomputed on
    #: read, for the reason the store stores its order totals: a promise that
    #: silently restated itself every time somebody opened the screen would be an
    #: estimate, and nobody could ever be late.
    promised_at = models.DateTimeField(null=True, blank=True)

    #: When the ticket may first be started. Set for a scheduled pickup, so that
    #: an order placed at lunchtime for 19:30 does not occupy a display screen
    #: all afternoon.
    start_after = models.DateTimeField(null=True, blank=True)

    started_at = models.DateTimeField(null=True, blank=True)
    ready_at = models.DateTimeField(null=True, blank=True)
    served_at = models.DateTimeField(null=True, blank=True)
    cancelled_at = models.DateTimeField(null=True, blank=True)

    note = models.CharField(max_length=300, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    #: Incremented on every transition, so a screen that read a ticket, decided
    #: something and bumped it can tell that somebody else moved it first.
    version = models.PositiveIntegerField(default=1)

    class Meta:
        db_table = "knight_restaurant_ticket"
        ordering = ("-created_at", "-id")
        indexes = [
            # The index the kitchen display query uses on every refresh, which on
            # a busy service is every few seconds from every screen in the room.
            models.Index(fields=["location", "state", "created_at"], name="knight_rest_ticket_board"),
            models.Index(fields=["state", "promised_at"], name="knight_rest_ticket_due"),
            models.Index(fields=["-number"], name="knight_rest_ticket_number"),
        ]

    @property
    def is_terminal(self) -> bool:
        return self.state in {TicketState.SERVED, TicketState.CANCELLED}

    def __str__(self) -> str:
        return f"#{self.number}"


class LineState(models.TextChoices):
    """
    Where one line is. Coarser than the ticket's on purpose.

    A line is queued, being made, or done. It has no `served`: lines are not
    handed over, tickets are, and giving a line a state it can never reach on its
    own is how a display screen ends up showing a ticket nobody can finish.
    """

    QUEUED = "queued", "Waiting"
    PREPARING = "preparing", "Being made"
    READY = "ready", "Done"
    CANCELLED = "cancelled", "Cancelled"


class TicketLine(models.Model):
    """
    One dish on a ticket, priced by nobody and timed by this Feature.

    There is no money here at all, which is the clearest statement of the split:
    what a dish costs is the store's order line and always will be. What this row
    knows is what it is called, how many, which station makes it and how long it
    was expected to take.

    `prep_minutes` and `load_units` are copied from the profile at the moment the
    ticket was opened rather than read through it. A chef who shortens a recipe's
    prep time this afternoon must not retroactively make this morning's tickets
    look late — the same snapshotting the store applies to prices.
    """

    ticket = models.ForeignKey(KitchenTicket, on_delete=models.CASCADE, related_name="lines")

    source_order_item_id = models.BigIntegerField(null=True, blank=True)
    sku = models.CharField(max_length=100, blank=True, default="")
    name = models.CharField(max_length=200)
    quantity = models.PositiveSmallIntegerField(default=1)

    station = models.ForeignKey(
        Station,
        on_delete=models.SET_NULL,
        null=True,
        blank=True,
        related_name="lines",
    )

    prep_minutes = models.PositiveSmallIntegerField(default=0)
    load_units = models.PositiveSmallIntegerField(default=0)

    #: What was asked for that is not on the menu: no onions, sauce on the side.
    #: The single most important field on a restaurant ticket and the one most
    #: often lost between the till and the kitchen.
    modifications = models.CharField(max_length=300, blank=True, default="")

    state = models.CharField(max_length=20, choices=LineState, default=LineState.QUEUED)
    started_at = models.DateTimeField(null=True, blank=True)
    ready_at = models.DateTimeField(null=True, blank=True)
    display_order = models.PositiveSmallIntegerField(default=0)

    class Meta:
        db_table = "knight_restaurant_ticket_line"
        ordering = ("display_order", "id")
        indexes = [
            models.Index(fields=["station", "state"], name="knight_rest_line_station"),
        ]
        constraints = [
            models.CheckConstraint(
                condition=models.Q(quantity__gt=0),
                name="knight_rest_line_has_quantity",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.quantity} × {self.name}"


class TicketEvent(models.Model):
    """
    Every state a ticket has held, and who moved it.

    Append-only, and written by the aggregate rather than by callers, so a ticket
    cannot change state without leaving a trace. It is what answers "this went
    out twenty minutes late, where did it sit" on a Monday morning, which is the
    only question anybody asks about a Saturday service.
    """

    ticket = models.ForeignKey(KitchenTicket, on_delete=models.CASCADE, related_name="events")
    from_state = models.CharField(max_length=20, blank=True, default="")
    to_state = models.CharField(max_length=20)

    #: Free text rather than a user id. A ticket is bumped by whoever is standing
    #: at the screen, and a foreign key to an account nobody creates would leave
    #: this empty for the common case — the store's own order history made the
    #: same call.
    actor = models.CharField(max_length=200, blank=True, default="")
    note = models.CharField(max_length=300, blank=True, default="")
    occurred_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_restaurant_ticket_event"
        ordering = ("occurred_at", "id")
        indexes = [
            models.Index(fields=["ticket", "occurred_at"], name="knight_rest_event_ticket"),
        ]

    def __str__(self) -> str:
        return f"{self.from_state or '—'} → {self.to_state}"


class CapacitySlot(models.Model):
    """
    A window the kitchen is willing to promise, and how much it will take in it.

    Rows rather than a rule evaluated on the fly, because a slot is a thing a
    restaurant edits: Friday at eight is not Tuesday at three, and the evening a
    coach party is booked is an evening with almost no capacity for anyone else.
    Generating them from opening hours is a convenience on top of the rows, never
    instead of them.

    `capacity_units` is in the same units as the prep profiles. What is left in a
    slot is derived from its bookings and never stored — there is no counter to
    drift, which is the argument `advanced-inventory` makes about stock and the
    two ledgers in phase 14 make about money and points.
    """

    starts_at = models.DateTimeField()
    minutes = models.PositiveSmallIntegerField(default=15)
    location = models.CharField(max_length=40, blank=True, default=DEFAULT_LOCATION)
    service = models.CharField(max_length=20, choices=ServiceStyle, default=ServiceStyle.COLLECTION)

    capacity_units = models.PositiveIntegerField(default=20)

    #: A slot closed by hand: the kitchen is short-staffed, the oven is broken, a
    #: private party has the room. Closed rather than deleted, so that whatever
    #: is already booked into it survives and staff can see why nothing new is
    #: going in.
    is_open = models.BooleanField(default=True)
    note = models.CharField(max_length=200, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_restaurant_slot"
        ordering = ("starts_at",)
        indexes = [
            models.Index(fields=["location", "starts_at"], name="knight_rest_slot_when"),
        ]
        constraints = [
            # One slot per moment per service per location. Two overlapping slots
            # for the same service would each think they had the whole kitchen,
            # which is the oversell this table exists to prevent.
            models.UniqueConstraint(
                fields=["location", "service", "starts_at"],
                name="knight_rest_one_slot_per_start",
            ),
            models.CheckConstraint(
                condition=models.Q(minutes__gt=0),
                name="knight_rest_slot_has_length",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.starts_at:%Y-%m-%d %H:%M} ({self.service})"


class BookingState(models.TextChoices):
    HELD = "held", "Held"
    CONFIRMED = "confirmed", "Confirmed"
    RELEASED = "released", "Released"
    EXPIRED = "expired", "Expired"


class SlotBooking(models.Model):
    """
    Somebody's claim on part of a slot.

    Held first and confirmed later, because a checkout has two moments — choosing
    the time and paying for it — and the gap between them is exactly where two
    people are told the same last slot is free. A held booking counts against the
    slot immediately and stops counting by *time* rather than by state, so a
    restaurant whose cron has never run still quotes honest times; the hourly
    worker tidies rows away and nothing more.
    """

    slot = models.ForeignKey(CapacitySlot, on_delete=models.CASCADE, related_name="bookings")

    #: The basket, order or ticket this claim is for. Unique per slot, so a
    #: retried checkout finds its own booking rather than taking the slot twice.
    reference = models.CharField(max_length=100)

    units = models.PositiveIntegerField(validators=[MinValueValidator(1)])
    state = models.CharField(max_length=12, choices=BookingState, default=BookingState.HELD)
    expires_at = models.DateTimeField()
    created_at = models.DateTimeField(auto_now_add=True)
    settled_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        db_table = "knight_restaurant_slot_booking"
        ordering = ("-created_at",)
        indexes = [
            models.Index(fields=["slot", "state"], name="knight_rest_booking_slot"),
            models.Index(fields=["state", "expires_at"], name="knight_rest_booking_expiry"),
            models.Index(fields=["reference"], name="knight_rest_booking_ref"),
        ]
        constraints = [
            models.UniqueConstraint(
                fields=["slot", "reference"],
                name="knight_rest_one_booking_per_reference",
            ),
            models.CheckConstraint(
                condition=models.Q(units__gt=0),
                name="knight_rest_booking_is_positive",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.units} of {self.slot_id} for {self.reference} ({self.state})"
