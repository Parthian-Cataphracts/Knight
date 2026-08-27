"""
What a store may ask this Feature to do.

The published surface. Callers pass codes, SKUs and order numbers and get plain
dataclasses and booleans back; nothing here returns a model, and nothing here
reads one of the store's.

Four things in this module carry the weight:

- **`describe()` names a code that already exists.** It never creates one, never
  rewrites one, and never asks another Feature to change. A code nobody has
  described is still a perfectly good code — an anonymous one — which is what
  makes this Feature adoptable a branch at a time.
- **`route()` decides once and writes it down.** Asked again about the same
  order, it returns the decision that was made rather than making a new one.
- **A location that is shut is never routed to.** Opening hours and closures are
  consulted at the moment of the decision, and the fallbacks are explicit and
  ordered rather than "the first row that came back".
- **An absent menu row means available.** The exception table is the design, and
  reading it the other way round would hide every newly created product from
  every branch.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date, datetime, time
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from django.db import transaction
from django.db.models import Q
from django.utils import timezone

from . import config
from .models import (
    Closure,
    Location,
    LocationKind,
    MenuAvailability,
    OpeningHours,
    OrderRouting,
    RoutingRule,
    RuleKind,
    StaffAssignment,
    StaffMember,
)


class LocationError(RuntimeError):
    """Something a caller asked for that this Feature will not do."""


class UnknownLocation(LocationError):
    """No location has that code."""


class UnknownStaffMember(LocationError):
    """No member of staff has that code."""


class NowhereToRouteTo(LocationError):
    """
    No location can take this order.

    Its own class because the caller's response is a decision rather than an
    error page: a checkout that hits this tells the shopper the shop is closed
    rather than accepting an order nobody will cook.
    """


@dataclass(frozen=True)
class Place:
    """One location, as anything outside this Feature sees it."""

    code: str
    name: str
    kind: str
    timezone: str
    city: str
    postal_code: str
    is_default: bool
    is_active: bool

    @property
    def takes_customers(self) -> bool:
        """Whether somebody can turn up at the door."""
        return self.kind in {LocationKind.SHOP, LocationKind.KITCHEN}


@dataclass(frozen=True)
class Decision:
    """Where an order went, and why."""

    order_number: int
    location: str
    reason: str
    decided_at: datetime


# --- Naming the codes -------------------------------------------------------


def define_location(code: str, *, name: str = "", **fields) -> Place:
    """
    Creates or updates a location. Idempotent on the code.

    This is the whole integration with every other Feature in the catalogue: the
    code is the same string `advanced-inventory` has been stamping on movements
    and `restaurant-operations` on tickets since before this Feature existed.
    Defining it here attaches a name, an address and opening hours to a code that
    was already in use, and defining it changes not one of their rows.
    """
    location, _ = Location.objects.update_or_create(
        code=_code(code),
        defaults={"name": name or code, **_clean(fields)},
    )

    return _place(location)


def describe(code: str) -> Place | None:
    """
    What a code means, or None when nobody has said.

    None rather than a raise, and this is the function that makes gradual
    adoption work. Every caller here — a stock report grouping movements by
    location, a floor screen labelling a kitchen — has to keep working for a code
    that has not been described yet, because on the day this Feature is installed
    every code is one of those.
    """
    location = Location.objects.filter(code=_code(code)).first()

    return _place(location) if location is not None else None


def places(*, active_only: bool = True, kind: str = "") -> list[Place]:
    """Every location a merchant has described."""
    found = Location.objects.all()

    if active_only:
        found = found.filter(is_active=True)

    if kind:
        found = found.filter(kind=kind)

    return [_place(location) for location in found]


@transaction.atomic
def set_default(code: str) -> Place:
    """
    Names the location an unmatched order goes to.

    Clears the previous one first, in one transaction, because the database
    enforces that exactly one row holds it — and a caller that set the new one
    before clearing the old would simply be refused. Doing it here rather than
    leaving it to callers means the refusal never reaches a merchant as a
    constraint name.
    """
    location = _location(code)

    Location.objects.filter(is_default=True).exclude(pk=location.pk).update(is_default=False)

    if not location.is_default:
        location.is_default = True
        location.save(update_fields=["is_default"])

    return _place(location)


def default_place() -> Place | None:
    """The location an unmatched order goes to, if a merchant has named one."""
    location = Location.objects.filter(is_default=True, is_active=True).first()

    return _place(location) if location is not None else None


# --- When a place is open ---------------------------------------------------


def set_hours(code: str, weekday: int, opens: time, closes: time) -> OpeningHours:
    """Adds or corrects one trading window. Monday is 0."""
    location = _location(code)

    window, _ = OpeningHours.objects.update_or_create(
        location=location,
        weekday=int(weekday),
        opens=opens,
        defaults={"closes": closes},
    )

    return window


def close_on(code: str, starts_on: date, ends_on: date | None = None, *, reason: str = "") -> Closure:
    """Shuts a location for a day or a run of days, whatever its hours say."""
    location = _location(code)

    return Closure.objects.create(
        location=location,
        starts_on=starts_on,
        ends_on=ends_on or starts_on,
        reason=reason.strip()[:200],
    )


def is_open(code: str, *, at: datetime | None = None) -> bool:
    """
    Whether a location is trading at a moment.

    Read in the location's **own** timezone where it has one. A merchant with a
    branch an hour ahead has opening hours that mean different moments in each,
    and evaluating both against the store's timezone would put one of them an
    hour wrong all year and two hours wrong for a fortnight either side of the
    clocks changing.

    A location with no hours at all is open, unless the configuration says
    otherwise. That is the deliberate reading for the same reason the menu table
    is an exception table: on the day this Feature is installed nobody has
    entered any hours, and a merchant whose every branch silently stopped
    accepting orders would rightly call it a broken release. A location that has
    hours on other days and none today is shut today, which is how a shop says
    "we do not open on Mondays".
    """
    at = at or timezone.now()
    location = _location(code)

    if not location.is_active:
        return False

    local = _local(at, location.timezone)

    if location.closures.filter(starts_on__lte=local.date(), ends_on__gte=local.date()).exists():
        return False

    windows = location.hours.filter(weekday=local.weekday())

    if not windows.exists():
        # No window today. Open only if this location has no hours anywhere -
        # nobody has said when it trades - and only while the configuration says
        # an undescribed location trades.
        return not location.hours.exists() and config.open_without_hours()

    return windows.filter(opens__lte=local.time(), closes__gt=local.time()).exists()


# --- Who works where --------------------------------------------------------


def define_staff(code: str, *, name: str = "", **fields) -> StaffMember:
    """Creates or updates a member of staff. Idempotent on the code."""
    staff, _ = StaffMember.objects.update_or_create(
        code=_code(code),
        defaults={"name": name or code, **_clean(fields)},
    )

    return staff


def assign(staff_code: str, location_code: str, *, role: str = "", starts_on: date | None = None) -> StaffAssignment:
    """
    Puts somebody on a location's rota.

    Idempotent on the open assignment: asking twice returns the one that is
    already open rather than being refused by the constraint, because the caller
    is usually a nightly sync of a rota that has not changed.
    """
    staff = _staff(staff_code)
    location = _location(location_code)

    existing = staff.assignments.filter(location=location, ends_on__isnull=True).first()

    if existing is not None:
        return existing

    return StaffAssignment.objects.create(
        staff=staff,
        location=location,
        role=role.strip()[:80],
        starts_on=starts_on or timezone.localdate(),
    )


def unassign(staff_code: str, location_code: str, *, ends_on: date | None = None) -> int:
    """
    Takes somebody off a rota, by dating the assignment rather than deleting it.

    "Who worked at the Camden branch last March" is a question a merchant asks
    after an incident, and a membership row that was deleted when they moved
    answers it with nobody.
    """
    return StaffAssignment.objects.filter(
        staff__code=_code(staff_code),
        location__code=_code(location_code),
        ends_on__isnull=True,
    ).update(ends_on=ends_on or timezone.localdate())


def roster(code: str, *, on: date | None = None) -> list[StaffMember]:
    """Who was assigned to a location on a date — today, unless asked otherwise."""
    on = on or timezone.localdate()

    return list(
        StaffMember.objects.filter(
            assignments__location__code=_code(code),
            assignments__starts_on__lte=on,
        )
        .filter(Q(assignments__ends_on__isnull=True) | Q(assignments__ends_on__gte=on))
        .distinct()
    )


# --- What each place sells --------------------------------------------------


def set_availability(code: str, sku: str, *, available: bool, note: str = "", object_id=None) -> MenuAvailability:
    """
    Records that one location does or does not sell one thing.

    Written only for the exceptions. A merchant never has to enumerate what a
    branch *does* sell, which is the difference between a menu that stays correct
    when a product is added and one that hides every new product until somebody
    remembers.
    """
    location = _location(code)

    entry, _ = MenuAvailability.objects.update_or_create(
        location=location,
        sku=_sku(sku),
        defaults={"is_available": bool(available), "note": note.strip()[:200], "object_id": object_id},
    )

    return entry


def sells(code: str, sku: str) -> bool:
    """
    Whether a location sells a thing.

    True when nothing has been said, because absence means available. Reading it
    the other way round would mean a store installing this Feature discovered
    that none of its branches sold anything.
    """
    entry = MenuAvailability.objects.filter(
        location__code=_code(code), sku=_sku(sku)
    ).first()

    return True if entry is None else entry.is_available


def unavailable_at(code: str) -> list[str]:
    """The SKUs one location has said it does not sell."""
    return list(
        MenuAvailability.objects.filter(location__code=_code(code), is_available=False)
        .order_by("sku")
        .values_list("sku", flat=True)
    )


# --- Where an order goes ----------------------------------------------------


def define_rule(kind: str, *, location: str, pattern: str = "", priority: int = 100) -> RoutingRule:
    """Creates or updates a routing rule. Idempotent on kind and pattern."""
    if kind not in RuleKind.values:
        raise LocationError(f"'{kind}' is not a routing rule this Feature knows.")

    rule, _ = RoutingRule.objects.update_or_create(
        kind=kind,
        pattern=_pattern(kind, pattern),
        defaults={"location": _location(location), "priority": int(priority)},
    )

    return rule


@transaction.atomic
def route(
    order_number: int,
    *,
    postal_code: str = "",
    city: str = "",
    zone: str = "",
    prefer: str = "",
    at: datetime | None = None,
) -> Decision:
    """
    Decides where one order is handled, once.

    Asked again about the same order it returns what was decided, because where
    an order was handled is a fact about that order rather than a function of the
    rules that exist today. That is the difference between a routing table and a
    report that rewrites itself.

    The order of resort is explicit, and each step is skipped when the location it
    names is shut:

    1. what the caller asked for, if anything — a shopper choosing a branch has
       already decided and nothing here should overrule them;
    2. the first matching rule, by priority;
    3. the default location;
    4. the only open location, if a merchant has exactly one.

    A closed location is never returned, at any step. An order routed to a shut
    branch is an order nobody cooks, discovered by the shopper rather than by the
    merchant.
    """
    at = at or timezone.now()
    number = _number(order_number)

    existing = OrderRouting.objects.filter(source_order_number=number).select_related("location").first()

    if existing is not None:
        return _decision(existing)

    chosen, rule, reason = _choose(postal_code=postal_code, city=city, zone=zone, prefer=prefer, at=at)

    if chosen is None:
        raise NowhereToRouteTo(
            f"Order {number} matched no open location; every candidate was shut or unnamed."
        )

    return _decision(
        OrderRouting.objects.create(
            source_order_number=number,
            location=chosen,
            rule=rule,
            reason=reason,
        )
    )


def routing_for(order_number: int) -> Decision | None:
    """Where an order went, or None if it was never routed."""
    found = (
        OrderRouting.objects.filter(source_order_number=_number(order_number))
        .select_related("location")
        .first()
    )

    return _decision(found) if found is not None else None


def orders_at(code: str, *, limit: int = 100) -> list[Decision]:
    """What was routed to one location, most recent first."""
    return [
        _decision(routing)
        for routing in OrderRouting.objects.filter(location__code=_code(code))
        .select_related("location")
        .order_by("-decided_at")[: max(1, limit)]
    ]


# --- Internals --------------------------------------------------------------


def _choose(*, postal_code: str, city: str, zone: str, prefer: str, at: datetime):
    """The ordered fallbacks in `route`'s docstring, and nothing else."""
    if prefer:
        preferred = Location.objects.filter(code=_code(prefer)).first()

        if preferred is not None and is_open(preferred.code, at=at):
            return preferred, None, "the branch the order asked for"

    for rule in RoutingRule.objects.filter(is_active=True).select_related("location").order_by("priority", "id"):
        if not _matches(rule, postal_code=postal_code, city=city, zone=zone):
            continue

        if not is_open(rule.location.code, at=at):
            # Skipped rather than failed. A rule pointing at a branch that is
            # shut on a Sunday is a correct rule on the other six days, and
            # refusing the order would be worse than sending it to the default.
            continue

        return rule.location, rule, f"{rule.kind}:{rule.pattern or '*'}"

    fallback = Location.objects.filter(is_default=True, is_active=True).first()

    if fallback is not None and is_open(fallback.code, at=at):
        return fallback, None, "the default location"

    open_now = [
        location
        for location in Location.objects.filter(is_active=True)
        if is_open(location.code, at=at)
    ]

    if len(open_now) == 1 and config.route_to_the_only_open_location():
        # A merchant with one open branch has not made a routing decision worth
        # asking them about, and refusing here would make this Feature break the
        # single-site case it was supposed to leave alone.
        return open_now[0], None, "the only open location"

    return None, None, ""


def _matches(rule: RoutingRule, *, postal_code: str, city: str, zone: str) -> bool:
    if rule.kind == RuleKind.ALWAYS:
        return True

    if rule.kind == RuleKind.POSTAL_PREFIX:
        return bool(postal_code) and _normalise(postal_code).startswith(_normalise(rule.pattern))

    if rule.kind == RuleKind.CITY:
        return bool(city) and _normalise(city) == _normalise(rule.pattern)

    if rule.kind == RuleKind.ZONE:
        return bool(zone) and _normalise(zone) == _normalise(rule.pattern)

    return False


def _local(at: datetime, name: str) -> datetime:
    """
    A moment as the location reads it.

    An unknown or empty timezone falls back to the store's, loudly in neither
    direction: a merchant who typed the name wrong gets the store's clock, which
    is what they had before this Feature existed, rather than an exception in the
    middle of a checkout.
    """
    if not name:
        return timezone.localtime(at)

    try:
        return at.astimezone(ZoneInfo(name))
    except (ZoneInfoNotFoundError, ValueError):
        return timezone.localtime(at)


def _place(location: Location) -> Place:
    return Place(
        code=location.code,
        name=location.name,
        kind=location.kind,
        timezone=location.timezone,
        city=location.city,
        postal_code=location.postal_code,
        is_default=location.is_default,
        is_active=location.is_active,
    )


def _decision(routing: OrderRouting) -> Decision:
    return Decision(
        order_number=routing.source_order_number,
        location=routing.location.code,
        reason=routing.reason,
        decided_at=routing.decided_at,
    )


def _location(code: str) -> Location:
    location = Location.objects.filter(code=_code(code)).first()

    if location is None:
        raise UnknownLocation(f"No location has the code '{code}'.")

    return location


def _staff(code: str) -> StaffMember:
    staff = StaffMember.objects.filter(code=_code(code)).first()

    if staff is None:
        raise UnknownStaffMember(f"No member of staff has the code '{code}'.")

    return staff


def _pattern(kind: str, pattern: str) -> str:
    """
    The pattern as it is stored.

    `always` keeps the empty string, because a rule that matches everything has
    nothing to match on and letting a stray pattern sit there would make two
    identical catch-alls look different to the unique constraint.
    """
    return "" if kind == RuleKind.ALWAYS else _normalise(pattern)


def _clean(fields: dict) -> dict:
    """Drops the keys a caller passed as None, so an omitted field keeps its value."""
    return {key: value for key, value in fields.items() if value is not None}


def _code(value: str) -> str:
    """
    Codes are matched however they were typed.

    Upper-cased and trimmed. The code is a join key shared with Features that
    have been stamping it on their own rows for two releases, so a second
    location created by a stray space would be a branch whose stock is somewhere
    else.
    """
    return str(value or "").strip().upper()


def _sku(value: str) -> str:
    return str(value or "").strip().upper()


def _normalise(value: str) -> str:
    """A pattern or a value to compare it against: case and spacing carry no meaning."""
    return "".join(str(value or "").split()).upper()


def _number(value) -> int:
    try:
        return int(value)
    except (TypeError, ValueError) as exc:
        raise LocationError(f"'{value}' is not an order number.") from exc
