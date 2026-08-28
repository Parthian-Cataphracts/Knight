"""
What a store may ask this Feature to do.

The published surface. Callers pass references, SKUs and amounts and get plain
dataclasses back; nothing here returns a model, and nothing here reads one of the
store's.

Five things carry the weight, and all five are about the same fear:

- **`bill_due()` opens a period before it charges one.** The period is the unit
  of idempotency, it is unique per subscription per sequence, and the database
  refuses the second one. Two workers, a webhook delivered twice and an operator
  running the job by hand during an incident all end at that constraint.
- **A charge is attempted at most once per period per run.** If an attempt row
  exists for this period and this attempt number, the work is already done.
- **Due-ness is time, not state.** `next_run_at <= now` decides what is billable.
  A store whose cron is broken bills late; a store whose billing depended on the
  cron having run would bill wrongly.
- **A pause moves the clock forward rather than accruing.** A shopper who pauses
  for three months and comes back is charged once, not four times, because
  charging for time nobody received is the single worst thing this Feature could
  do.
- **Nothing charges without a provider that says it can.** The default provider
  refuses, loudly and by name, rather than pretending money moved.

    `store` is the first argument of every request-driven function in this
    module, and it is not optional. It is what makes one deployment able to
    serve many shops: a caller that forgot it would not compile rather than
    quietly read another store's data (`adr/0033`).
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date, datetime, timedelta
from decimal import Decimal

from django.db import IntegrityError, transaction
from django.db.models import Q, Sum
from django.utils import timezone

from . import config, providers
from .models import (
    ALLOWED_TRANSITIONS,
    AttemptOutcome,
    BILLABLE,
    BillingAttempt,
    BillingPeriod,
    DEFAULT_LOCATION,
    Interval,
    PeriodState,
    Subscription,
    SubscriptionEvent,
    SubscriptionLine,
    SubscriptionOrder,
    SubscriptionState,
)

ZERO = Decimal("0.00")

#: How many days each interval advances the clock. Months and years are handled
#: separately, because "a month" is not thirty days and a subscription that drifted
#: by two days a year would eventually bill on a different day of the month than
#: the one a shopper agreed to.
DAYS = {Interval.DAILY: 1, Interval.WEEKLY: 7}


class SubscriptionError(RuntimeError):
    """Something a caller asked for that this Feature will not do."""


class UnknownSubscription(SubscriptionError):
    """No subscription has that reference."""


class InvalidTransition(SubscriptionError):
    """A subscription cannot go from where it is to where it was pushed."""


class AlreadyBilled(SubscriptionError):
    """
    That period has already been charged.

    Its own class because it is the *good* outcome of a retry, not a fault. A
    caller that treats it as an error will alert on a webhook being delivered
    twice, which is a thing webhooks do.
    """


@dataclass(frozen=True)
class Charge:
    """What one billing run did to one subscription."""

    reference: str
    sequence: int
    amount: Decimal
    outcome: str
    detail: str = ""
    provider_reference: str = ""

    @property
    def succeeded(self) -> bool:
        return self.outcome == AttemptOutcome.SUCCEEDED


@dataclass(frozen=True)
class Summary:
    """A subscription as anything outside this Feature sees it."""

    reference: str
    state: str
    interval: str
    interval_count: int
    currency: str
    amount: Decimal
    next_run_at: datetime | None
    periods_billed: int
    paid_to_date: Decimal
    provider: str
    location: str

    @property
    def is_billable(self) -> bool:
        return self.state in BILLABLE


# --- Setting one up ---------------------------------------------------------


@transaction.atomic
def create(
    store,
    reference: str,
    *,
    amount,
    lines=(),
    interval: str = Interval.MONTHLY,
    interval_count: int = 1,
    currency: str = "",
    shopper_id: int | None = None,
    display_name: str = "",
    email: str = "",
    provider: str = "",
    payment_method_reference: str = "",
    starts_on: date | None = None,
    ends_on: date | None = None,
    periods_limit: int | None = None,
    location: str = DEFAULT_LOCATION,
) -> Summary:
    """
    Records an agreement to be charged repeatedly.

    It starts `pending` and bills nothing. Activation is a separate act, because
    the moment money starts moving should be a decision somebody made rather than
    a side effect of a record being created — a store that creates a subscription
    while a shopper is still on the payment page would otherwise have charged
    them before they finished.

    Idempotent on the reference: a checkout retried after a timeout finds the
    subscription it already created rather than making a second one, which for a
    Feature that charges people is the difference between a retry and a
    duplicate.
    """
    reference = _reference(reference)
    existing = Subscription.objects.filter(store=store, reference=reference).first()

    if existing is not None:
        return summarise(existing)

    if interval not in Interval.values:
        raise SubscriptionError(f"'{interval}' is not an interval this Feature knows.")

    subscription = Subscription.objects.create(
        store=store,
        reference=reference,
        source_shopper_id=shopper_id,
        display_name=display_name.strip()[:200],
        email=email.strip()[:254],
        interval=interval,
        interval_count=max(1, int(interval_count or 1)),
        currency=(currency or config.currency())[:3].upper(),
        amount=_money(amount),
        provider=provider or config.provider(),
        payment_method_reference=payment_method_reference.strip()[:200],
        started_on=starts_on,
        ends_on=ends_on,
        periods_limit=periods_limit,
        location=location,
    )

    for index, line in enumerate(lines):
        SubscriptionLine.objects.create(
            subscription=subscription,
            source_product_id=line.get("source_product_id"),
            source_variant_id=line.get("source_variant_id"),
            sku=(line.get("sku") or "").strip().upper()[:100],
            name=(line.get("name") or "Item")[:200],
            quantity=max(1, int(line.get("quantity") or 1)),
            unit_price=_money(line.get("unit_price", 0), allow_zero=True),
            display_order=index,
        )

    _record(subscription, "", subscription.state, actor="", reason="created")

    return summarise(subscription)


@transaction.atomic
def activate(store, reference: str, *, actor: str = "", now=None) -> Summary:
    """
    Starts the clock.

    The first period becomes due immediately unless a start date says otherwise,
    which is what a shopper expects: they have just agreed to buy something and
    the first one should arrive.
    """
    now = now or timezone.now()
    subscription = _locked(store, reference)

    _transition(subscription, SubscriptionState.ACTIVE, actor=actor, reason="activated", now=now)

    if subscription.next_run_at is None:
        starts = subscription.started_on

        subscription.next_run_at = (
            timezone.make_aware(datetime.combine(starts, datetime.min.time()))
            if starts and starts > timezone.localdate(now)
            else now
        )

    subscription.started_on = subscription.started_on or timezone.localdate(now)
    subscription.save(update_fields=["next_run_at", "started_on", "updated_at"])

    return summarise(subscription)


# --- Pausing, resuming, stopping --------------------------------------------


@transaction.atomic
def pause(store, reference: str, *, actor: str = "", reason: str = "", now=None) -> Summary:
    """
    Stops billing without ending the agreement.

    The clock stops rather than continuing to tick, which is the whole point: a
    shopper who pauses in March and returns in June must be charged for June, not
    for March, April, May and June. Every subscription bug that makes the news is
    a version of that.
    """
    now = now or timezone.now()
    subscription = _locked(store, reference)

    _transition(subscription, SubscriptionState.PAUSED, actor=actor, reason=reason, now=now)

    subscription.paused_at = now
    subscription.next_run_at = None
    subscription.save(update_fields=["paused_at", "next_run_at", "updated_at"])

    return summarise(subscription)


@transaction.atomic
def resume(store, reference: str, *, actor: str = "", now=None) -> Summary:
    """
    Starts billing again, from now — or from the end of what they have already
    paid for, whichever is later.

    Never from where the clock stopped. Resuming into the past would open every
    period the pause skipped and charge for all of them, which is the behaviour a
    shopper reads as being robbed for going on holiday. And never *before* the
    period they are already inside has run out, which would charge them twice for
    one month — see `_resume_at`.
    """
    now = now or timezone.now()
    subscription = _locked(store, reference)

    _transition(subscription, SubscriptionState.ACTIVE, actor=actor, reason="resumed", now=now)

    subscription.paused_at = None
    subscription.next_run_at = _resume_at(subscription, now)
    subscription.save(update_fields=["paused_at", "next_run_at", "updated_at"])

    return summarise(subscription)


def _resume_at(subscription: Subscription, now):
    """
    When a resumed subscription becomes due again.

    Now, **or the day after the period already paid for ends** — whichever is
    later. A shopper who pauses five minutes after being billed has paid for the
    month they are in, and making them due immediately on resume would charge
    them again for time they already own.

    That is the mirror of the rule `resume` is famous for. Resuming into the past
    charges for a pause nobody used; resuming into an already-paid period charges
    twice for one month. The first is the bug everybody writes about and the
    second is the one that is easy to write while fixing it.
    """
    paid = (
        subscription.periods.filter(state=PeriodState.PAID).order_by("-ends_on").first()
    )

    if paid is None:
        return now

    next_start = timezone.make_aware(
        datetime.combine(paid.ends_on + timedelta(days=1), datetime.min.time())
    )

    return max(now, next_start)


@transaction.atomic
def cancel(store, reference: str, *, actor: str = "", reason: str = "", now=None) -> Summary:
    """
    Ends the agreement.

    Nothing is refunded and nothing is deleted. A period already paid stays paid
    and stays in the ledger: a cancellation is the end of the future, not a
    rewriting of the past, and a merchant asked about a charge from last month
    still has to be able to explain it.
    """
    now = now or timezone.now()
    subscription = _locked(store, reference)

    _transition(subscription, SubscriptionState.CANCELLED, actor=actor, reason=reason, now=now)

    subscription.cancelled_at = now
    subscription.cancellation_reason = reason.strip()[:500]
    subscription.next_run_at = None
    subscription.save(
        update_fields=["cancelled_at", "cancellation_reason", "next_run_at", "updated_at"]
    )

    return summarise(subscription)


# --- Billing ----------------------------------------------------------------


def due(store=None, *, now=None, location: str | None = None):
    """
    The subscriptions that may be billed at this moment.

    By time and by state, in one predicate, so that "which subscriptions do we
    charge tonight" has exactly one answer in this package. A paused subscription
    has no `next_run_at` at all, so it cannot appear here even if the state check
    were wrong.
    """
    now = now or timezone.now()
    found = Subscription.objects.filter(state__in=list(BILLABLE), next_run_at__lte=now)

    # Scoped when a store is asking about its own, unscoped when the billing
    # worker is asking about everything it has to charge tonight. Both callers
    # are real and they want different answers, so the argument is optional here
    # and required everywhere a store's own request reaches.
    if store is not None:
        found = found.filter(store=store)

    if location is not None:
        found = found.filter(location=location)

    return found.order_by("next_run_at", "id")


@transaction.atomic
def bill(store, reference: str, *, now=None, force: bool = False) -> Charge:
    """
    Charges one subscription for one period.

    The order of what happens here is the Feature:

    1. **lock the subscription row.** Everything after this is serialised against
       another worker doing the same thing;
    2. **check it may be billed at all** — active or past-due, and due by the
       clock unless a caller has explicitly forced it;
    3. **open the next period**, which is where the database refuses a duplicate;
    4. **ask the provider**, exactly once;
    5. **record the attempt whatever happened**, because an attempt nobody wrote
       down is a charge nobody can explain.

    A period that is already paid is not an error. `AlreadyBilled` exists so a
    caller can tell "this is a retry and everything is fine" from "the card was
    declined", and treating those the same is how a shop alerts on webhooks being
    delivered twice.
    """
    now = now or timezone.now()
    subscription = _locked(store, reference)

    if not subscription.is_billable:
        return _refuse(subscription, None, f"the subscription is {subscription.state}", now)

    if not force and (subscription.next_run_at is None or subscription.next_run_at > now):
        return _refuse(subscription, None, "nothing is due yet", now)

    period = _open_period(subscription, now)

    if period.state == PeriodState.PAID:
        raise AlreadyBilled(f"Period {period.sequence} of {subscription.reference} is already paid.")

    return _charge(subscription, period, now)


def bill_due(*, now=None, limit: int = 500, location: str | None = None) -> dict[str, int]:
    """
    Charges everything that is due. The entrypoint of the hourly worker.

    Each subscription is billed in its own transaction, deliberately. One
    shopper's declined card must not roll back the twenty charges that already
    succeeded before it, and a single transaction around a batch is how a
    provider timeout turns into a whole night's billing being lost.
    """
    now = now or timezone.now()
    counts = {"billed": 0, "failed": 0, "refused": 0}

    # (store, reference) rather than reference alone: the worker crosses every
    # shop this service holds, and two of them numbering from SUB-1 is normal.
    # Pulling the reference on its own would have billed whichever one the
    # database happened to return first.
    wanted = list(due(now=now, location=location).values_list("store_id", "reference")[:limit])

    for store_id, reference in wanted:
        store = _store(store_id)

        try:
            # This store's provider, currency and retry policy — not the
            # previous one's. The worker crosses shops, so the configuration has
            # to be re-entered for each.
            with config.use(store):
                charge = bill(store, reference, now=now)
        except AlreadyBilled:
            # Somebody else got there first. Not a failure and not a charge.
            counts["refused"] += 1
            continue
        except SubscriptionError:
            counts["refused"] += 1
            continue

        if charge.succeeded:
            counts["billed"] += 1
        elif charge.outcome == AttemptOutcome.FAILED:
            counts["failed"] += 1
        else:
            counts["refused"] += 1

    return counts


def retry_failed(*, now=None, limit: int = 500) -> dict[str, int]:
    """
    Tries the periods whose retry has come round. The entrypoint of the daily worker.

    Retries are scheduled by time on the period, so this is tidying in the same
    sense the other Features' workers are: nothing here decides *whether* a
    period is owed, only that now is a reasonable moment to ask again.

    A period whose attempts are exhausted stops being retried and its
    subscription becomes `unpaid` — a different state from `past_due`, because a
    merchant needs to tell "we are chasing this" from "we have stopped".
    """
    now = now or timezone.now()
    counts = {"recovered": 0, "failed": 0, "given_up": 0}

    periods = list(
        BillingPeriod.objects.filter(
            state=PeriodState.FAILED, retry_at__isnull=False, retry_at__lte=now
        )
        .select_related("subscription")
        .order_by("retry_at")[:limit]
    )

    for period in periods:
        subscription = period.subscription

        if not subscription.is_billable:
            continue

        with transaction.atomic():
            locked = _locked(subscription.store, subscription.reference)
            fresh = BillingPeriod.objects.select_for_update().get(pk=period.pk)

            if fresh.state != PeriodState.FAILED:
                continue

            if fresh.attempt_count >= config.max_attempts():
                fresh.retry_at = None
                fresh.save(update_fields=["retry_at"])

                _transition(
                    locked,
                    SubscriptionState.UNPAID,
                    actor="billing",
                    reason=f"period {fresh.sequence} was not paid after {fresh.attempt_count} attempts",
                    now=now,
                )
                locked.next_run_at = None
                locked.save(update_fields=["next_run_at", "updated_at"])
                counts["given_up"] += 1
                continue

            charge = _charge(locked, fresh, now)

        counts["recovered" if charge.succeeded else "failed"] += 1

    return counts


# --- What the store turns a paid period into --------------------------------

#: What separates a subscription's reference from a period's sequence inside an
#: order reference. A character no reference may contain, so the two halves can
#: always be told apart again.
ORDER_REFERENCE_SEPARATOR = "#"


def order_reference(period: BillingPeriod) -> str:
    """
    The opaque string a store carries on the order it makes for this period.

    Opaque **to the store**, which is the whole point: the store puts it on the
    order's `external_reference`, announces it back with `order.placed`, and
    never interprets it. This service reads it, because a period is this
    service's idea and working out which one an order was for is its job.

    Naming the period rather than only the subscription is what makes the loop
    exact. "The oldest period still owing an order" is a good guess and a guess
    is not good enough when two orders for two periods are created in one batch
    and their deliveries arrive in the other order.
    """
    return f"{period.subscription.reference}{ORDER_REFERENCE_SEPARATOR}{period.sequence}"


def parse_order_reference(value: str) -> tuple[str, int | None]:
    """
    The subscription reference and period a store handed back, if it named one.

    Never raises. Whatever arrives is somebody else's string — a merchant typing
    a reference on an order by hand is a real case — so an unparseable half is a
    period this service does not know about rather than a 500.
    """
    reference = _reference(value)

    if ORDER_REFERENCE_SEPARATOR not in reference:
        return reference, None

    head, _, tail = reference.rpartition(ORDER_REFERENCE_SEPARATOR)

    try:
        sequence = int(tail)
    except ValueError:
        return reference, None

    return head.strip(), sequence if sequence > 0 else None


def periods_awaiting_orders(store=None, *, limit: int = 200):
    """
    Paid periods the store has not yet made an order for.

    The seam, from this side. This Feature may not create an order — orders are
    the store's, and a Feature that wrote them would be one the store could not
    uninstall — so it names what is owed and the store's own command creates it.

    `store` is optional here and only here: a worker inside this service sweeps
    every shop at once, and the endpoint a store calls always passes its own.
    """
    found = BillingPeriod.objects.filter(state=PeriodState.PAID, order__isnull=True)

    if store is not None:
        found = found.filter(subscription__store=store)

    return list(
        found.select_related("subscription")
        .prefetch_related("subscription__lines")
        .order_by("settled_at")[:limit]
    )


@transaction.atomic
def record_order(store, reference: str, sequence: int, order_number: int) -> SubscriptionOrder:
    """
    Records the order a store made for a paid period.

    Idempotent, and it has to be: a store runs its generator from cron, and a
    second run that made a second order would send a shopper two boxes for one
    payment.
    """
    period = _period(store, reference, sequence)
    number = int(order_number)
    existing = SubscriptionOrder.objects.filter(period=period).first()

    if existing is not None:
        if existing.source_order_number != number:
            # A second, *different* order for a period that already has one. Not
            # a retry: somebody has sent a shopper two boxes for one payment, or
            # is about to, and saying so is more use than quietly keeping the
            # first.
            raise SubscriptionError(
                f"{reference} period {sequence} is already order "
                f"{existing.source_order_number}."
            )

        return existing

    claimed = SubscriptionOrder.objects.filter(store=store, source_order_number=number).first()

    if claimed is not None:
        # The same order number against two periods. The unique constraint would
        # refuse it anyway; refusing it here makes it a 409 the store can read
        # rather than a 500 that looks like this service is broken.
        raise SubscriptionError(f"Order {number} is already period {claimed.period.sequence}.")

    return SubscriptionOrder.objects.create(
        period=period, store=store, source_order_number=number
    )


# --- Reading ----------------------------------------------------------------


def summarise(subscription: Subscription | str, store=None) -> Summary:
    """One subscription, with the figures derived rather than stored."""
    if isinstance(subscription, str):
        if store is None:
            raise SubscriptionError("Looking a subscription up by reference needs the store it belongs to.")

        subscription = _require(store, subscription)

    paid = subscription.periods.filter(state=PeriodState.PAID)

    return Summary(
        reference=subscription.reference,
        state=subscription.state,
        interval=subscription.interval,
        interval_count=subscription.interval_count,
        currency=subscription.currency,
        amount=subscription.amount,
        next_run_at=subscription.next_run_at,
        periods_billed=paid.count(),
        paid_to_date=paid.aggregate(total=Sum("amount"))["total"] or ZERO,
        provider=subscription.provider,
        location=subscription.location,
    )


def history(store, reference: str) -> list[SubscriptionEvent]:
    """Everywhere a subscription has been, in order."""
    return list(_require(store, reference).events.all())


def periods(store, reference: str) -> list[BillingPeriod]:
    """Every period, paid or not, with its attempts."""
    return list(_require(store, reference).periods.prefetch_related("attempts").all())


def lines(store, reference: str) -> list[SubscriptionLine]:
    return list(_require(store, reference).lines.all())


# --- Workers ----------------------------------------------------------------


def run_billing() -> dict[str, int]:
    """Entrypoint for the hourly worker the manifest declares."""
    return bill_due()


def run_retries() -> dict[str, int]:
    """Entrypoint for the daily worker the manifest declares."""
    return retry_failed()


# --- Internals --------------------------------------------------------------


def _charge(subscription: Subscription, period: BillingPeriod, now) -> Charge:
    """
    Asks the provider once, records what happened, and moves the clock.

    Everything that decides money is in here rather than spread across the two
    callers, so there is exactly one place to read when somebody asks how a
    charge is made.
    """
    attempt_number = period.attempt_count + 1
    result = providers.charge(
        provider=subscription.provider,
        amount=period.amount,
        currency=period.currency,
        reference=f"{subscription.reference}#{period.sequence}",
        payment_method_reference=subscription.payment_method_reference,
    )

    BillingAttempt.objects.create(
        period=period,
        attempt=attempt_number,
        outcome=result.outcome,
        provider=subscription.provider,
        provider_reference=result.provider_reference[:200],
        detail=result.detail[:500],
        amount=period.amount,
    )

    period.attempt_count = attempt_number

    if result.outcome == AttemptOutcome.SUCCEEDED:
        period.state = PeriodState.PAID
        period.settled_at = now
        period.retry_at = None
        period.save(update_fields=["state", "settled_at", "attempt_count", "retry_at"])

        _advance(subscription, period, now)
    else:
        period.state = PeriodState.FAILED

        # Always scheduled, including after the final attempt, and that is not an
        # oversight. The decision to give up is made by the retry pass, and a
        # period with no `retry_at` drops out of the query that pass makes - so
        # setting it to null here would leave the subscription stuck in
        # `past_due` for ever, chased by nobody and never marked unpaid.
        period.retry_at = now + timedelta(days=config.retry_after_days(attempt_number))
        period.save(update_fields=["state", "attempt_count", "retry_at"])

        if subscription.state == SubscriptionState.ACTIVE:
            _transition(
                subscription,
                SubscriptionState.PAST_DUE,
                actor="billing",
                reason=result.detail[:500],
                now=now,
            )

        # The clock stops while a period is unpaid. Advancing it would open the
        # next period on top of one nobody has paid for, and the shopper would owe
        # two.
        subscription.next_run_at = None
        subscription.save(update_fields=["next_run_at", "updated_at"])

    return Charge(
        reference=subscription.reference,
        sequence=period.sequence,
        amount=period.amount,
        outcome=result.outcome,
        detail=result.detail,
        provider_reference=result.provider_reference,
    )


def _advance(subscription: Subscription, period: BillingPeriod, now) -> None:
    """
    Moves a subscription on after a period is paid.

    Where it ends is decided here too, because "twelve boxes then stop" is a
    promise a merchant made and a thirteenth charge would break it.
    """
    subscription.last_period = period.sequence

    reached_limit = (
        subscription.periods_limit is not None and period.sequence >= subscription.periods_limit
    )
    reached_end = subscription.ends_on is not None and period.ends_on >= subscription.ends_on

    if reached_limit or reached_end:
        subscription.next_run_at = None
        subscription.save(update_fields=["last_period", "next_run_at", "updated_at"])
        _transition(
            subscription,
            SubscriptionState.ENDED,
            actor="billing",
            reason="the agreement ran to its end",
            now=now,
        )

        return

    if subscription.state == SubscriptionState.PAST_DUE:
        _transition(subscription, SubscriptionState.ACTIVE, actor="billing", reason="paid", now=now)

    # The day after this period ends, which is when the next one starts. Adding
    # another interval to the *end* date would put the clock a whole interval
    # past where the next period actually begins - a monthly subscription would
    # bill in two months rather than one, every time.
    subscription.next_run_at = timezone.make_aware(
        datetime.combine(period.ends_on + timedelta(days=1), datetime.min.time())
    )
    subscription.save(update_fields=["last_period", "next_run_at", "updated_at"])


def _open_period(subscription: Subscription, now) -> BillingPeriod:
    """
    The period this billing run is for.

    An open unpaid period is reused rather than a new one opened beside it — a
    retry is another attempt at the same debt, never a second debt. Otherwise the
    next sequence number is taken under the row lock the caller already holds,
    and the unique constraint is what makes that safe against anything holding no
    lock at all.
    """
    unpaid = subscription.periods.filter(
        state__in=[PeriodState.PENDING, PeriodState.FAILED]
    ).order_by("sequence").first()

    if unpaid is not None:
        return unpaid

    sequence = subscription.last_period + 1
    starts_on = _period_start(subscription, now)

    try:
        return BillingPeriod.objects.create(
            subscription=subscription,
            sequence=sequence,
            starts_on=starts_on,
            ends_on=_next_start(starts_on, subscription) - timedelta(days=1),
            currency=subscription.currency,
            amount=subscription.amount,
        )
    except IntegrityError as exc:
        # Somebody without the lock opened it between the read and the write.
        # The constraint did its job; this turns it into a sentence.
        raise AlreadyBilled(
            f"Period {sequence} of {subscription.reference} was opened by something else."
        ) from exc


def _period_start(subscription: Subscription, now) -> date:
    last = subscription.periods.order_by("-sequence").first()

    if last is not None:
        return last.ends_on + timedelta(days=1)

    return subscription.started_on or timezone.localdate(now)


def _next_start(starts_on: date, subscription: Subscription | None = None) -> date:
    """
    One interval on from a date.

    Months and years are added by calendar rather than by days, because a monthly
    subscription taken out on the 15th should be billed on the 15th — and 30 days
    drifts it into the previous month twice a year. A day that does not exist in
    the target month lands on that month's last day, which is what every billing
    system that has thought about it does with the 31st.
    """
    interval = subscription.interval if subscription else Interval.MONTHLY
    count = subscription.interval_count if subscription else 1

    if interval in DAYS:
        return starts_on + timedelta(days=DAYS[interval] * count)

    months = count if interval == Interval.MONTHLY else count * 12
    month_index = starts_on.month - 1 + months
    year = starts_on.year + month_index // 12
    month = month_index % 12 + 1
    day = min(starts_on.day, _days_in(year, month))

    return date(year, month, day)


def _days_in(year: int, month: int) -> int:
    from calendar import monthrange

    return monthrange(year, month)[1]


def _refuse(subscription: Subscription, period: BillingPeriod | None, why: str, now) -> Charge:
    """
    Records that this Feature declined to ask for money, and why.

    Its own outcome rather than a kind of failure. A failure is the provider
    saying no; this is us not asking, and reporting the two together would make a
    merchant think their payments were being declined when in fact nothing was
    ever due.
    """
    if period is not None:
        BillingAttempt.objects.create(
            period=period,
            attempt=period.attempt_count + 1,
            outcome=AttemptOutcome.REFUSED,
            provider=subscription.provider,
            detail=why[:500],
            amount=period.amount,
        )

    return Charge(
        reference=subscription.reference,
        sequence=period.sequence if period else 0,
        amount=period.amount if period else ZERO,
        outcome=AttemptOutcome.REFUSED,
        detail=why,
    )


def _transition(subscription: Subscription, target: str, *, actor: str, reason: str, now) -> None:
    """
    Moves a subscription on, and records that it moved.

    The event is written here rather than by callers, so a subscription cannot
    change state without leaving a trace. On a Feature that takes money that
    trace is not diagnostics — it is the answer to "I never agreed to that".
    """
    if target == subscription.state:
        return

    if target not in ALLOWED_TRANSITIONS[subscription.state]:
        raise InvalidTransition(
            f"A subscription that is {subscription.state} cannot become {target}."
        )

    previous = subscription.state
    subscription.state = target
    subscription.version += 1
    subscription.save(update_fields=["state", "version", "updated_at"])

    _record(subscription, previous, target, actor=actor, reason=reason)


def _record(subscription: Subscription, previous: str, target: str, *, actor: str, reason: str):
    return SubscriptionEvent.objects.create(
        subscription=subscription,
        from_state=previous,
        to_state=target,
        actor=actor.strip()[:200],
        reason=reason.strip()[:500],
    )


def _locked(store, reference: str) -> Subscription:
    """
    The subscription, with its row locked.

    Every path that can move money takes this first. Two workers reaching the
    same subscription at the same moment are serialised here, and the unique
    constraint on the period is what protects against anything that did not come
    through this function at all.
    """
    found = (
        Subscription.objects.select_for_update()
        .filter(store=store, reference=_reference(reference))
        .first()
    )

    if found is None:
        raise UnknownSubscription(f"No subscription has the reference '{reference}'.")

    return found


def _require(store, reference: str) -> Subscription:
    found = Subscription.objects.filter(store=store, reference=_reference(reference)).first()

    if found is None:
        raise UnknownSubscription(f"No subscription has the reference '{reference}'.")

    return found


def _period(store, reference: str, sequence: int) -> BillingPeriod:
    found = BillingPeriod.objects.filter(
        subscription__store=store,
        subscription__reference=_reference(reference),
        sequence=int(sequence),
    ).first()

    if found is None:
        raise SubscriptionError(f"{reference} has no period {sequence}.")

    return found


def _store(store_id):
    """
    A store row from its primary key, for the workers.

    They read `store_id` off the subscription rather than the whole related
    object, because the alternative is a join on a query that already runs
    against every shop's subscriptions at once.
    """
    from knightlink.models import Store

    return Store.objects.get(pk=store_id)


def _reference(value: str) -> str:
    return str(value or "").strip()


def _money(value, *, allow_zero: bool = False) -> Decimal:
    try:
        amount = Decimal(str(value)).quantize(Decimal("0.01"))
    except (TypeError, ArithmeticError) as exc:
        raise SubscriptionError(f"'{value}' is not an amount.") from exc

    if amount < 0:
        raise SubscriptionError("An amount cannot be negative.")

    if amount == 0 and not allow_zero:
        raise SubscriptionError("A subscription for nothing is not a subscription.")

    return amount
