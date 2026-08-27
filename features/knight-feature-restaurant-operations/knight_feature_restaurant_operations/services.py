"""
What a store may ask this Feature to do.

The published surface. Callers pass order numbers, SKUs and quantities and get
numbers and plain dataclasses back; nothing here returns a model, and nothing
here reads one of the store's.

Four things in this module carry the weight:

- **`promise()` takes a maximum and adds a backlog.** The longest dish on the
  ticket is how long the ticket takes; what the kitchen is already holding is why
  it will take longer than that. Summing the dishes would quote forty minutes for
  four things that arrive together in twelve.
- **`book()` is the only function that has to be right under concurrency**, and
  the only one that takes a lock. Two checkouts reaching the last space in a slot
  both read "room for one" and both write a booking, unless something serialises
  them.
- **`remaining()` counts holds by time, not by state.** A restaurant whose cron
  has never run still quotes honest slots; the hourly worker removes dead rows
  and changes no answer.
- **Nothing is recalculated into a stored total.** A promise is stored because it
  is a statement somebody made at a moment. Every other number here is derived
  when it is asked for.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date, datetime, time, timedelta

from django.db import transaction
from django.db.models import Q, Sum
from django.utils import timezone

from . import config
from .models import (
    ALLOWED_TRANSITIONS,
    BookingState,
    CapacitySlot,
    DEFAULT_LOCATION,
    KitchenTicket,
    LineState,
    PrepProfile,
    ServiceArea,
    ServiceStyle,
    SlotBooking,
    Station,
    Table,
    TableSession,
    TicketEvent,
    TicketLine,
    TicketNumberSequence,
    TicketState,
)

#: A session the nightly sweep closed, rather than one somebody closed by taking
#: payment. Kept apart because a report that could not tell them apart would
#: count tables nobody cleared as covers that were served.
ABANDONED = "abandoned"
SETTLED = "settled"

#: The states a ticket is in while it is still the kitchen's problem. Read by
#: every board query and by the backlog arithmetic, so it is named once.
LIVE_TICKET_STATES = {TicketState.SCHEDULED, TicketState.QUEUED, TicketState.PREPARING}

#: A booking that still counts against a slot. `held` counts too, and that is the
#: point of holding it.
LIVE_BOOKING_STATES = {BookingState.HELD, BookingState.CONFIRMED}


class RestaurantError(RuntimeError):
    """Something a caller asked for that this Feature will not do."""


class UnknownTable(RestaurantError):
    """No table has that code."""


class UnknownTicket(RestaurantError):
    """No ticket has that number."""


class UnknownSlot(RestaurantError):
    """No slot starts at that time for that service."""


class TableInUse(RestaurantError):
    """
    Somebody is already sitting there.

    Its own class because the caller's response is a decision rather than an
    error page: a till that hits this offers to add to the open session instead
    of starting a second one.
    """


class InvalidTransition(RestaurantError):
    """A ticket cannot go from where it is to where it was pushed."""


class NoCapacity(RestaurantError):
    """
    The kitchen will not promise that slot.

    Carries the numbers, because a caller showing "fully booked" and a caller
    showing "only room for one more" need the same refusal and different words.
    """

    def __init__(self, starts_at: datetime, requested: int, remaining: int) -> None:
        super().__init__(
            f"{starts_at:%Y-%m-%d %H:%M}: {requested} unit(s) requested and {remaining} left."
        )
        self.starts_at = starts_at
        self.requested = requested
        self.remaining = remaining


@dataclass(frozen=True)
class Offer:
    """One time the kitchen is willing to promise, and how much room is in it."""

    starts_at: datetime
    minutes: int
    service: str
    location: str
    capacity_units: int
    remaining_units: int

    @property
    def is_free(self) -> bool:
        return self.remaining_units > 0


@dataclass(frozen=True)
class TableStatus:
    """One table as the floor screen shows it."""

    code: str
    name: str
    area: str
    seats: int
    is_seated: bool
    party_size: int
    label: str
    seated_minutes: int
    open_tickets: int


@dataclass(frozen=True)
class Load:
    """What the kitchen is carrying right now."""

    location: str
    live_tickets: int
    outstanding_units: int
    throughput_units_per_hour: int

    @property
    def backlog_minutes(self) -> int:
        """
        How long the queue in front of a new ticket takes to clear.

        Rounded up, because a kitchen that is 90% of a minute behind is a minute
        behind as far as anybody waiting is concerned, and a promise that rounded
        down would be late by construction on every busy service.
        """
        if self.outstanding_units <= 0:
            return 0

        per_hour = max(1, self.throughput_units_per_hour)

        return -(-self.outstanding_units * 60 // per_hour)


# --- The room ---------------------------------------------------------------


def define_area(code: str, *, name: str = "", **fields) -> ServiceArea:
    """Creates or updates a part of the room. Idempotent on the code."""
    area, _ = ServiceArea.objects.update_or_create(
        code=_code(code),
        defaults={"name": name or code, **_clean(fields)},
    )

    return area


def define_table(code: str, *, name: str = "", area: str = "", **fields) -> Table:
    """
    Creates or updates a table. Idempotent on the code.

    The area is passed as a code rather than a row, so a store setting its floor
    up from a fixture never has to hold this Feature's models.
    """
    defaults = {"name": name, **_clean(fields)}

    if area:
        defaults["area"] = ServiceArea.objects.filter(code=_code(area)).first()

    table, _ = Table.objects.update_or_create(code=_code(code), defaults=defaults)

    return table


@transaction.atomic
def seat(code: str, *, party_size: int = 1, label: str = "") -> TableSession:
    """
    Sits a party at a table.

    Refuses rather than opening a second session, and refuses with `TableInUse`
    rather than a generic error, because the caller's next move is to add to the
    session that is already open. The database would refuse this anyway — the
    partial unique index is the actual guarantee — and this turns that into a
    sentence somebody can act on.
    """
    table = _table(code)

    if not table.is_active:
        raise RestaurantError(f"Table {table.code} is out of service.")

    open_session = table.sessions.filter(closed_at__isnull=True).first()

    if open_session is not None:
        raise TableInUse(f"Table {table.code} already has a party sitting at it.")

    return TableSession.objects.create(
        table=table,
        party_size=max(1, int(party_size or 1)),
        label=label.strip(),
    )


@transaction.atomic
def clear(code: str, *, reason: str = SETTLED, now=None) -> TableSession | None:
    """
    Closes the open session at a table.

    Returns None rather than raising when there is nothing to close: clearing a
    table twice is what happens when two members of staff both tidy up, and it is
    not a mistake worth an error page.

    Tickets still open on the session are left alone deliberately. Food that is
    on the grill is on the grill whatever the till says, and closing a session
    that silently cancelled it would take a dish out of the kitchen's sight while
    it was still cooking.
    """
    now = now or timezone.now()
    table = _table(code)
    session = table.sessions.filter(closed_at__isnull=True).first()

    if session is None:
        return None

    session.closed_at = now
    session.closed_reason = reason
    session.save(update_fields=["closed_at", "closed_reason"])

    return session


def floor(*, location: str = DEFAULT_LOCATION, now=None) -> list[TableStatus]:
    """
    Every table and what is happening at it, for a floor screen.

    One query for the tables and one for the open sessions, rather than a
    per-table lookup: a floor screen refreshes every few seconds from every
    handset in the room, and this is the query it makes.
    """
    now = now or timezone.now()
    tables = list(
        Table.objects.filter(location=location, is_active=True).select_related("area")
    )
    sessions = {
        session.table_id: session
        for session in TableSession.objects.filter(
            table__in=tables, closed_at__isnull=True
        )
    }
    live = _live_ticket_counts(sessions.values())

    statuses = []

    for table in tables:
        session = sessions.get(table.pk)
        statuses.append(
            TableStatus(
                code=table.code,
                name=table.name or table.code,
                area=table.area.code if table.area_id else "",
                seats=table.seats,
                is_seated=session is not None,
                party_size=session.party_size if session else 0,
                label=session.label if session else "",
                seated_minutes=(
                    int((now - session.opened_at).total_seconds() // 60) if session else 0
                ),
                open_tickets=live.get(session.pk, 0) if session else 0,
            )
        )

    return statuses


# --- The kitchen ------------------------------------------------------------


def define_station(code: str, *, name: str = "", **fields) -> Station:
    """Creates or updates a station. Idempotent on the code."""
    station, _ = Station.objects.update_or_create(
        code=_code(code),
        defaults={"name": name or code, **_clean(fields)},
    )

    return station


def define_prep(sku: str, *, name: str = "", station: str = "", **fields) -> PrepProfile:
    """
    Records how long one sellable thing takes and how much of the kitchen it
    uses.

    The seam the store's own catalogue pushes through — the Feature may not read
    `apps.catalog`, so the store hands the definitions over, exactly as it does
    for `advanced-inventory`. Idempotent on the SKU, so re-running a sync after a
    menu change corrects the profile and touches no ticket that has already been
    printed.
    """
    defaults = {"name": name or sku, **_clean(fields)}

    if station:
        defaults["station"] = Station.objects.filter(code=_code(station)).first()

    profile, _ = PrepProfile.objects.update_or_create(sku=_sku(sku), defaults=defaults)

    return profile


def load(*, location: str = DEFAULT_LOCATION, now=None) -> Load:
    """
    What the kitchen is carrying: the work on tickets it has not finished.

    Scheduled tickets whose time has not come are excluded. A pre-order for
    tonight is not weight on the kitchen at three in the afternoon, and counting
    it would make every promise made this afternoon an hour too pessimistic.
    """
    now = now or timezone.now()

    # One predicate rather than two queries unioned: a union subquery is harder
    # for the planner and, more to the point, harder for the next person to read
    # than the sentence it is trying to say — "what the kitchen may be working on
    # now".
    working_on = Q(location=location) & (
        Q(state__in=[TicketState.QUEUED, TicketState.PREPARING])
        | Q(state=TicketState.SCHEDULED, start_after__lte=now)
    )
    tickets = KitchenTicket.objects.filter(working_on)

    outstanding = (
        TicketLine.objects.filter(ticket__in=tickets)
        .exclude(state__in=[LineState.READY, LineState.CANCELLED])
        .aggregate(total=Sum("load_units"))["total"]
        or 0
    )

    return Load(
        location=location,
        live_tickets=tickets.count(),
        outstanding_units=int(outstanding),
        throughput_units_per_hour=_throughput(location),
    )


def promise(lines, *, location: str = DEFAULT_LOCATION, now=None) -> datetime:
    """
    When the kitchen expects a ticket of these lines to be done.

    The whole arithmetic, in one place, because it is the number a restaurant is
    judged on:

        longest single dish  +  time to clear what is already queued

    Not the sum of the dishes. A kitchen cooks in parallel, and a shop that
    quotes the sum is wrong twice: too slow when it promises and too early when
    the food lands. Quantity multiplies the *load*, not the minutes — a second
    portion of the same dish goes in the same pan.
    """
    now = now or timezone.now()
    slowest = max((_line_minutes(line) for line in lines), default=0)

    return now + timedelta(minutes=slowest + load(location=location, now=now).backlog_minutes)


@transaction.atomic
def open_ticket(
    lines,
    *,
    order_number: int | None = None,
    service: str = ServiceStyle.DINE_IN,
    table: str = "",
    location: str = DEFAULT_LOCATION,
    start_after: datetime | None = None,
    note: str = "",
    now=None,
) -> KitchenTicket:
    """
    Puts one ticket in front of the kitchen.

    `lines` is a list of plain dictionaries — `{sku, object_id, name, quantity,
    modifications, source_order_item_id}` — so a caller never constructs a model
    of this Feature's. Either `sku` or `object_id` identifies the dish, because a
    till knows the SKU and a store's order line knows a variant id. Everything the ticket needs to be timed is copied from the
    prep profile at this moment rather than read through it later: a chef who
    shortens a recipe this afternoon must not retroactively make this morning's
    tickets look late.

    A ticket with a `start_after` in the future opens `scheduled` rather than
    `queued`. That is the difference between a pre-order and a display screen
    with ninety minutes of tomorrow's lunch on it.
    """
    now = now or timezone.now()

    if not lines:
        raise RestaurantError("A ticket needs at least one line.")

    session = None

    if table:
        session = _table(table).sessions.filter(closed_at__isnull=True).first()

        if session is None:
            raise RestaurantError(
                f"Table {table} has nobody sitting at it; seat the party before ordering."
            )

    scheduled = start_after is not None and start_after > now

    ticket = KitchenTicket.objects.create(
        number=TicketNumberSequence.take(),
        source_order_number=order_number,
        service=service,
        state=TicketState.SCHEDULED if scheduled else TicketState.QUEUED,
        location=location,
        session=session,
        start_after=start_after,
        note=note.strip()[:300],
        # Promised from when the kitchen may start, not from now. A pre-order for
        # 19:30 is not forty minutes late because it was placed at lunchtime.
        promised_at=promise(lines, location=location, now=start_after if scheduled else now),
    )

    for index, line in enumerate(lines):
        profile = _profile(line.get("sku", ""), line.get("object_id"))
        quantity = max(1, int(line.get("quantity") or 1))

        TicketLine.objects.create(
            ticket=ticket,
            source_order_item_id=line.get("source_order_item_id"),
            sku=_sku(line.get("sku", "")),
            name=(line.get("name") or (profile.name if profile else "") or "Item")[:200],
            quantity=quantity,
            station=profile.station if profile else None,
            prep_minutes=_profile_minutes(profile),
            load_units=_profile_load(profile) * quantity,
            modifications=(line.get("modifications") or "").strip()[:300],
            display_order=index,
        )

    _record(ticket, "", ticket.state, actor="", note="opened")

    return ticket


@transaction.atomic
def advance(number: int, target: str, *, actor: str = "", note: str = "", now=None) -> KitchenTicket:
    """
    Moves a ticket on, and records that it moved.

    The event is written here rather than by the caller, so a ticket cannot
    change state without leaving a trace — the same call the store's own order
    aggregate makes. That trace is what answers "this went out twenty minutes
    late, where did it sit", which is the only question anybody asks about a
    Saturday service.

    Bumping a ticket to `preparing` or `ready` carries its lines with it. A
    kitchen display has one button per ticket, and lines that stayed behind would
    be a queue of phantom work nobody can clear.
    """
    now = now or timezone.now()
    ticket = _ticket(number, lock=True)

    if target not in ALLOWED_TRANSITIONS[ticket.state]:
        raise InvalidTransition(
            f"A ticket that is {ticket.state} cannot become {target}."
        )

    previous = ticket.state
    ticket.state = target
    ticket.version += 1

    if target == TicketState.PREPARING:
        # Only the first time. A dish sent back and re-fired keeps the moment the
        # kitchen first picked the ticket up, because that is what the service
        # time is measured from.
        ticket.started_at = ticket.started_at or now
        ticket.lines.filter(state=LineState.QUEUED).update(
            state=LineState.PREPARING, started_at=now
        )
    elif target == TicketState.READY:
        ticket.ready_at = now
        ticket.lines.exclude(state__in=[LineState.READY, LineState.CANCELLED]).update(
            state=LineState.READY, ready_at=now
        )
    elif target == TicketState.SERVED:
        ticket.served_at = now
    elif target == TicketState.CANCELLED:
        ticket.cancelled_at = now
        ticket.lines.exclude(state=LineState.READY).update(state=LineState.CANCELLED)

    ticket.save(
        update_fields=[
            "state",
            "version",
            "started_at",
            "ready_at",
            "served_at",
            "cancelled_at",
            "updated_at",
        ]
    )
    _record(ticket, previous, target, actor=actor, note=note)

    return ticket


@transaction.atomic
def bump_line(line_id: int, *, state: str = LineState.READY, actor: str = "", now=None) -> TicketLine:
    """
    Moves one line, and lets the ticket follow if that was the last of them.

    Derived where it can be, exactly as `advanced-inventory` derives a purchase
    order's state from its lines: a ticket is `ready` because everything on it
    is, not because somebody said so. The two states that stay decisions —
    serving it and cancelling it — are still `advance()`'s.
    """
    now = now or timezone.now()
    line = TicketLine.objects.select_for_update().select_related("ticket").filter(pk=line_id).first()

    if line is None:
        raise RestaurantError(f"No ticket line has the id {line_id}.")

    line.state = state

    if state == LineState.PREPARING:
        line.started_at = line.started_at or now
    elif state == LineState.READY:
        line.ready_at = now

    line.save(update_fields=["state", "started_at", "ready_at"])

    ticket = line.ticket

    # A line that has been touched means the kitchen has picked the ticket up,
    # whichever state the line went to. A ticket left `queued` while one of its
    # dishes was already plated would sit at the top of the board looking
    # unstarted, which is the one thing a board must never lie about.
    if state in {LineState.PREPARING, LineState.READY} and ticket.state == TicketState.QUEUED:
        advance(ticket.number, TicketState.PREPARING, actor=actor, now=now)
        ticket.refresh_from_db()

    outstanding = ticket.lines.exclude(state__in=[LineState.READY, LineState.CANCELLED])

    if not outstanding.exists() and ticket.state == TicketState.PREPARING:
        advance(ticket.number, TicketState.READY, actor=actor, note="every line done", now=now)

    line.refresh_from_db()

    return line


def board(
    *,
    location: str = DEFAULT_LOCATION,
    station: str = "",
    now=None,
) -> list[KitchenTicket]:
    """
    What is on the screen: everything the kitchen still has to make.

    Scheduled tickets appear when their time comes, **by time rather than by
    state**. That ordering is the same one `advanced-inventory` insists on for
    expiring holds: a restaurant whose worker has never run still sees the right
    tickets, and the worker only tidies the stored state up behind it.

    Oldest first, which is the opposite of the model's default ordering and the
    only order a kitchen works in.
    """
    now = now or timezone.now()

    tickets = KitchenTicket.objects.filter(
        Q(location=location)
        & (
            Q(state__in=[TicketState.QUEUED, TicketState.PREPARING, TicketState.READY])
            | Q(state=TicketState.SCHEDULED, start_after__lte=now)
        )
    )

    if station:
        tickets = tickets.filter(lines__station__code=_code(station)).distinct()

    return list(
        tickets.order_by("created_at", "id").prefetch_related("lines__station").select_related("session__table")
    )


def ticket(number: int) -> KitchenTicket:
    """One ticket, for a screen showing a single order."""
    return _ticket(number)


def history(number: int) -> list[TicketEvent]:
    """Everywhere a ticket has been, in order."""
    return list(_ticket(number).events.all())


def release_scheduled(*, now=None) -> int:
    """
    Moves scheduled tickets whose time has come into the queue.

    Declared as the hourly worker, and **tidying rather than correctness**:
    `board()` already shows these by time, so a restaurant whose cron never runs
    still cooks the right food. What this buys is that the stored state matches
    what the screens show, which is what every report afterwards reads.
    """
    now = now or timezone.now()
    due = list(
        KitchenTicket.objects.filter(
            state=TicketState.SCHEDULED, start_after__lte=now
        ).values_list("number", flat=True)
    )

    for number in due:
        advance(number, TicketState.QUEUED, actor="scheduler", note="its time came", now=now)

    return len(due)


def close_abandoned_sessions(*, now=None) -> int:
    """
    Closes table sessions nobody cleared. Declared as the daily worker.

    A session left open holds a table out of service — the partial unique index
    means nobody can be seated there — and the most common cause is a party that
    left while staff were busy. Closed as `abandoned` rather than `settled`, so
    that a covers report never counts a table nobody served.
    """
    now = now or timezone.now()
    cutoff = now - timedelta(hours=config.abandon_after_hours())

    return TableSession.objects.filter(
        closed_at__isnull=True, opened_at__lte=cutoff
    ).update(closed_at=now, closed_reason=ABANDONED)


# --- Throttling and pickup times --------------------------------------------


def ensure_slots(
    day: date,
    *,
    opens: time,
    closes: time,
    service: str = ServiceStyle.COLLECTION,
    location: str = DEFAULT_LOCATION,
    minutes: int | None = None,
    capacity_units: int | None = None,
) -> list[CapacitySlot]:
    """
    Lays out a day's slots, if they are not there already.

    A convenience on top of the rows and never instead of them: it creates what
    is missing and leaves what exists untouched, so a slot a manager closed or
    shrank for a coach party survives the next time anybody generates the week.
    """
    minutes = minutes or config.slot_minutes()
    capacity = capacity_units if capacity_units is not None else config.slot_capacity()

    current = timezone.make_aware(datetime.combine(day, opens))
    end = timezone.make_aware(datetime.combine(day, closes))
    created = []

    while current < end:
        slot, was_created = CapacitySlot.objects.get_or_create(
            location=location,
            service=service,
            starts_at=current,
            defaults={"minutes": minutes, "capacity_units": capacity},
        )

        if was_created:
            created.append(slot)

        current += timedelta(minutes=minutes)

    return created


def taken(slot: CapacitySlot, *, now=None) -> int:
    """
    How much of a slot is already promised.

    Held bookings count, and they stop counting when they expire **by time**, not
    when a job gets round to them. That ordering is the whole reason the quoted
    times stay honest between cron runs.
    """
    now = now or timezone.now()

    return int(
        slot.bookings.filter(
            Q(state=BookingState.CONFIRMED)
            | Q(state=BookingState.HELD, expires_at__gt=now)
        ).aggregate(total=Sum("units"))["total"]
        or 0
    )


def remaining(slot: CapacitySlot, *, now=None) -> int:
    """What is left in a slot. Never negative in what it reports."""
    if not slot.is_open:
        return 0

    return max(0, slot.capacity_units - taken(slot, now=now))


def offers(
    *,
    units: int = 1,
    service: str = ServiceStyle.COLLECTION,
    location: str = DEFAULT_LOCATION,
    within_hours: int | None = None,
    now=None,
) -> list[Offer]:
    """
    The times a shopper may be shown.

    Slots that have already started are excluded, and so are slots with no room
    for what is being asked for. A time offered that cannot be taken is worse
    than no time at all: it is the checkout equivalent of a menu with nothing
    behind it.
    """
    now = now or timezone.now()
    horizon = now + timedelta(hours=within_hours or config.booking_horizon_hours())

    slots = (
        CapacitySlot.objects.filter(
            location=location,
            service=service,
            is_open=True,
            starts_at__gt=now,
            starts_at__lte=horizon,
        )
        .order_by("starts_at")
        .prefetch_related("bookings")
    )

    found = []

    for slot in slots:
        left = remaining(slot, now=now)

        if left >= max(1, units):
            found.append(
                Offer(
                    starts_at=slot.starts_at,
                    minutes=slot.minutes,
                    service=slot.service,
                    location=slot.location,
                    capacity_units=slot.capacity_units,
                    remaining_units=left,
                )
            )

    return found


@transaction.atomic
def book(
    starts_at: datetime,
    *,
    reference: str,
    units: int = 1,
    service: str = ServiceStyle.COLLECTION,
    location: str = DEFAULT_LOCATION,
    hold_minutes: int | None = None,
    now=None,
) -> SlotBooking:
    """
    Takes part of a slot for one basket or order.

    **The one function here that has to be correct under concurrency**, and the
    only one that takes a lock. Two checkouts reaching the last space in a slot
    both read "room for one" and both write a booking, unless something
    serialises them — and there is no constraint that can express "the sum of
    these rows must not exceed that number".

    So it locks the slot row with `select_for_update` first. Everything after
    that — the sum of the live bookings, the comparison, the insert — happens
    with every other booker of that slot waiting. The lock is on the slot rather
    than the table, so booking Friday at eight never waits for somebody booking
    Tuesday at three.

    Idempotent on the reference. A checkout retried after a timeout finds its own
    booking and gets it back rather than taking the slot twice, which the unique
    constraint would refuse anyway — this makes the good path a return instead of
    an error.
    """
    now = now or timezone.now()
    units = max(1, int(units or 1))
    minutes = config.hold_minutes() if hold_minutes is None else max(1, hold_minutes)

    if not reference.strip():
        raise RestaurantError("A booking has to say what it is for.")

    # The lock. Nothing below this line races with another booker of this slot,
    # and nothing above it needed to be protected.
    slot = (
        CapacitySlot.objects.select_for_update()
        .filter(location=location, service=service, starts_at=starts_at)
        .first()
    )

    if slot is None:
        raise UnknownSlot(f"No {service} slot starts at {starts_at:%Y-%m-%d %H:%M}.")

    if not slot.is_open:
        raise NoCapacity(slot.starts_at, units, 0)

    existing = slot.bookings.filter(reference=reference.strip()).first()

    if existing is not None:
        if existing.state in LIVE_BOOKING_STATES and (
            existing.state == BookingState.CONFIRMED or existing.expires_at > now
        ):
            return existing

        # A settled booking with this reference is not a hold to extend. It is a
        # caller reusing an order number for a second thing, which is a mistake
        # worth naming rather than absorbing.
        raise RestaurantError(
            f"'{reference}' already has a {existing.state} booking for that slot."
        )

    left = max(0, slot.capacity_units - taken(slot, now=now))

    if left < units:
        raise NoCapacity(slot.starts_at, units, left)

    return SlotBooking.objects.create(
        slot=slot,
        reference=reference.strip(),
        units=units,
        expires_at=now + timedelta(minutes=minutes),
    )


@transaction.atomic
def confirm(reference: str, *, now=None) -> int:
    """
    Turns a held slot into a kept one: the payment went through.

    Idempotent — a payment webhook delivered twice must not need two slots —
    because the second call finds the booking already confirmed and changes
    nothing.
    """
    now = now or timezone.now()

    return SlotBooking.objects.filter(
        reference=reference.strip(), state=BookingState.HELD
    ).update(state=BookingState.CONFIRMED, settled_at=now)


@transaction.atomic
def cancel_booking(reference: str, *, now=None) -> int:
    """Gives a slot back: an abandoned basket, a cancelled order, a failed card."""
    now = now or timezone.now()

    return SlotBooking.objects.filter(
        reference=reference.strip(), state__in=list(LIVE_BOOKING_STATES)
    ).update(state=BookingState.RELEASED, settled_at=now)


def expire_bookings(*, now=None) -> int:
    """
    Ends holds whose time is up. Declared as the hourly worker.

    Safe to run twice and safe to have not run: `taken()` already excludes an
    expired hold, so this is tidying rather than correctness. That order matters
    — a slot that only freed up when a job ran would put a restaurant's booking
    diary at the mercy of a cron entry.
    """
    now = now or timezone.now()

    return SlotBooking.objects.filter(
        state=BookingState.HELD, expires_at__lte=now
    ).update(state=BookingState.EXPIRED, settled_at=now)


# --- Workers ----------------------------------------------------------------


def run_slot_expiry() -> dict[str, int]:
    """Entrypoint for the hourly worker the manifest declares."""
    return {"expired": expire_bookings(), "released": release_scheduled()}


def run_service_sweep() -> dict[str, int]:
    """Entrypoint for the daily worker the manifest declares."""
    return {"sessions_closed": close_abandoned_sessions()}


# --- Internals --------------------------------------------------------------


def _record(ticket: KitchenTicket, previous: str, target: str, *, actor: str, note: str) -> TicketEvent:
    return TicketEvent.objects.create(
        ticket=ticket,
        from_state=previous,
        to_state=target,
        actor=actor.strip()[:200],
        note=note.strip()[:300],
    )


def _live_ticket_counts(sessions) -> dict[int, int]:
    ids = [session.pk for session in sessions]

    if not ids:
        return {}

    counts: dict[int, int] = {}

    for session_id, state in KitchenTicket.objects.filter(
        session_id__in=ids, state__in=list(LIVE_TICKET_STATES)
    ).values_list("session_id", "state"):
        counts[session_id] = counts.get(session_id, 0) + 1

    return counts


def _table(code: str) -> Table:
    table = Table.objects.filter(code=_code(code)).first()

    if table is None:
        raise UnknownTable(f"No table has the code '{code}'.")

    return table


def _ticket(number: int, *, lock: bool = False) -> KitchenTicket:
    query = KitchenTicket.objects.select_for_update() if lock else KitchenTicket.objects
    found = query.filter(number=_number(number)).first()

    if found is None:
        raise UnknownTicket(f"No ticket has the number {number}.")

    return found


def _profile(sku: str = "", object_id=None) -> PrepProfile | None:
    """
    The profile for a requested line, by SKU or by the store's own row id.

    Both, because the two callers have different halves of the identity. A till
    or a kitchen screen knows the SKU; the store's *order* lines do not carry one
    — they snapshot a variant id — and requiring a SKU there would mean every
    ticket opened from a real order was timed as though nobody had measured the
    dish.
    """
    if sku:
        found = PrepProfile.objects.filter(sku=_sku(sku)).select_related("station").first()

        if found is not None:
            return found

    if object_id in (None, ""):
        return None

    return PrepProfile.objects.filter(object_id=object_id).select_related("station").first()


def _profile_minutes(profile: PrepProfile | None) -> int:
    """
    How long one of these takes.

    An unknown SKU gets the configured default rather than zero. Zero would make
    a ticket of things nobody has profiled look instant, which is the worst
    possible default: the kitchen would be promised as empty precisely when it is
    handling something nobody has measured.
    """
    if profile is None:
        return config.default_prep_minutes()

    return 0 if not profile.is_prepared else profile.prep_minutes


def _profile_load(profile: PrepProfile | None) -> int:
    if profile is None:
        return config.default_load_units()

    return 0 if not profile.is_prepared else profile.load_units


def _line_minutes(line) -> int:
    """The minutes one requested line contributes to the promise."""
    if isinstance(line, TicketLine):
        return line.prep_minutes

    return _profile_minutes(_profile(line.get("sku", ""), line.get("object_id")))


def _throughput(location: str) -> int:
    """
    What this kitchen clears in an hour.

    The sum of its active stations where any are defined, and the configured
    figure where none are. A restaurant that has not described its kitchen still
    gets throttling; one that has gets throttling that knows the grill is the
    bottleneck.
    """
    total = (
        Station.objects.filter(location=location, is_active=True).aggregate(
            total=Sum("throughput_units_per_hour")
        )["total"]
        or 0
    )

    return int(total) or config.throughput_units_per_hour()


def _clean(fields: dict) -> dict:
    """Drops the keys a caller passed as None, so an omitted field keeps its value."""
    return {key: value for key, value in fields.items() if value is not None}


def _code(value: str) -> str:
    """
    Codes are matched however they were typed.

    Upper-cased and trimmed, because a table is "12" on one handset and " 12 " on
    another, and a second table row created by a stray space is a table nobody
    can find.
    """
    return str(value or "").strip().upper()


def _sku(value: str) -> str:
    return str(value or "").strip().upper()


def _number(value) -> int:
    try:
        return int(value)
    except (TypeError, ValueError) as exc:
        raise UnknownTicket(f"'{value}' is not a ticket number.") from exc
