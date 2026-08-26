"""
Earning, redeeming, and expiring points.

The surface the store calls. Everything that moves points does so inside a
transaction with the account row locked, because the two ways a loyalty ledger
goes wrong are both races: the same order earning twice, and two concurrent
redemptions each seeing a balance that only one of them can have.

Idempotency is by **constraint**, not by checking first. Two concurrent
checkouts both reading "not yet earned" is exactly how a customer ends up with
double points, and the database is the only place that race can be settled.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import timedelta
from decimal import Decimal

from django.db import IntegrityError, transaction
from django.db.models import Sum
from django.utils import timezone

from .models import Account, Programme, Tier, Transaction, TransactionKind


class LoyaltyError(RuntimeError):
    """A redemption that cannot be honoured. Carries a reason a shopper can read."""


@dataclass(frozen=True)
class Balance:
    """
    What a customer has, and what it is worth.

    `expiring_soon` is here rather than left to the caller because it is the one
    number that makes a loyalty programme work: points nobody is told are about
    to expire are points that quietly become a complaint.
    """

    subject: str
    points: int
    value: Decimal
    lifetime_points: int
    tier_name: str = ""
    expiring_soon: int = 0

    @property
    def is_empty(self) -> bool:
        return self.points <= 0


@dataclass(frozen=True)
class Movement:
    """The result of an earn or a redeem, in terms the store can record."""

    points: int
    balance: int
    duplicate: bool = False

    @property
    def applied(self) -> bool:
        return self.points != 0 and not self.duplicate


def account_for(subject: str) -> Account:
    """The customer's membership, created the first time they earn anything."""
    return Account.objects.get_or_create(subject=subject)[0]


@transaction.atomic
def earn(subject: str, *, amount: Decimal, source_order_id: int, reason: str = "") -> Movement:
    """
    Awards points for money spent.

    Returns a `duplicate` movement rather than raising when this order has
    already earned. A retried checkout is an ordinary event and must not be an
    error the store has to catch, nor points the customer gets twice.
    """
    programme = Programme.current()

    if not programme.is_active:
        return Movement(points=0, balance=balance_of(subject).points)

    account = _locked(subject)
    tier = account.tier

    base = programme.points_for(amount)
    multiplier = tier.earn_multiplier if tier is not None else Decimal("1")
    points = int(Decimal(base) * multiplier)

    if points <= 0:
        return Movement(points=0, balance=_balance_points(account))

    expires_at = (
        timezone.now() + timedelta(days=30 * programme.expiry_months)
        if programme.expiry_months > 0
        else None
    )

    try:
        # A savepoint of its own. An IntegrityError marks the whole transaction
        # broken in PostgreSQL, so without the inner atomic() the duplicate
        # branch below could not run a single query - it would raise
        # TransactionManagementError instead of reporting the duplicate, and the
        # retried checkout that is meant to be harmless would fail.
        with transaction.atomic():
            Transaction.objects.create(
                account=account,
                kind=TransactionKind.EARN,
                points=points,
                points_remaining=points,
                expires_at=expires_at,
                source_order_id=source_order_id,
                reason=reason,
            )
    except IntegrityError:
        # Already earned for this order. Settled by the constraint rather than
        # by a check the other request would also have passed.
        return Movement(points=0, balance=_balance_points(account), duplicate=True)

    account.lifetime_points += points
    _apply_tier(account)
    account.save(update_fields=["lifetime_points", "tier", "updated_at"])

    return Movement(points=points, balance=_balance_points(account))


@transaction.atomic
def redeem(subject: str, *, points: int, source_order_id: int, reason: str = "") -> Movement:
    """
    Spends points, oldest lots first.

    Refuses rather than partially applying. A shopper who asked to spend 500
    points and got 300 spent has been given an outcome nobody chose, and the
    checkout that called this cannot price an order it did not ask for.
    """
    programme = Programme.current()

    if not programme.is_active:
        raise LoyaltyError("The loyalty programme is not running.")

    if points <= 0:
        raise LoyaltyError("Redeeming needs a positive number of points.")

    if points < programme.minimum_redemption_points:
        raise LoyaltyError(
            f"Redemption starts at {programme.minimum_redemption_points} points."
        )

    account = _locked(subject)
    available = _balance_points(account)

    if available < points:
        raise LoyaltyError(f"That is {points - available} points more than the balance.")

    try:
        # Its own savepoint, for the same reason as `earn`.
        with transaction.atomic():
            Transaction.objects.create(
                account=account,
                kind=TransactionKind.REDEEM,
                points=-points,
                source_order_id=source_order_id,
                reason=reason,
            )
    except IntegrityError:
        return Movement(points=0, balance=available, duplicate=True)

    _consume(account, points)

    return Movement(points=-points, balance=_balance_points(account))


@transaction.atomic
def refund(subject: str, *, source_order_id: int, reason: str = "") -> Movement:
    """
    Undoes what an order did, when it is cancelled.

    Written as new ledger rows rather than by deleting the old ones. A ledger
    that can be edited answers no question worth asking, and "why did this
    balance change" has to stay answerable after a refund.

    Returned points come back as a **fresh lot** with a fresh expiry, which is
    generous and deliberate: reinstating the original expiry would sometimes
    hand a customer points that expired while the store was deciding.
    """
    account = _locked(subject)
    moved = 0

    earned = Transaction.objects.filter(
        account=account, kind=TransactionKind.EARN, source_order_id=source_order_id
    ).first()

    if earned is not None and earned.points_remaining > 0:
        # Only what is left. Points already spent on another order are gone, and
        # clawing them back would take a shopper's balance negative.
        clawback = earned.points_remaining
        Transaction.objects.create(
            account=account,
            kind=TransactionKind.ADJUST,
            points=-clawback,
            reason=reason or f"Order {source_order_id} cancelled",
        )
        earned.points_remaining = 0
        earned.save(update_fields=["points_remaining"])
        moved -= clawback

    spent = Transaction.objects.filter(
        account=account, kind=TransactionKind.REDEEM, source_order_id=source_order_id
    ).first()

    if spent is not None:
        programme = Programme.current()
        returned = abs(spent.points)
        expires_at = (
            timezone.now() + timedelta(days=30 * programme.expiry_months)
            if programme.expiry_months > 0
            else None
        )
        Transaction.objects.create(
            account=account,
            kind=TransactionKind.ADJUST,
            points=returned,
            points_remaining=returned,
            expires_at=expires_at,
            reason=reason or f"Order {source_order_id} cancelled",
        )
        moved += returned

    return Movement(points=moved, balance=_balance_points(account))


@transaction.atomic
def adjust(subject: str, *, points: int, reason: str) -> Movement:
    """
    A staff correction, named and audited.

    Exists so that putting points back is a transaction with a reason on it,
    rather than somebody editing a number. A reason is required for the same
    reason.
    """
    if points == 0:
        raise LoyaltyError("An adjustment of zero is not an adjustment.")

    if not reason.strip():
        raise LoyaltyError("An adjustment needs a reason.")

    account = _locked(subject)

    if points < 0 and _balance_points(account) < abs(points):
        raise LoyaltyError("That would take the balance below zero.")

    programme = Programme.current()
    expires_at = (
        timezone.now() + timedelta(days=30 * programme.expiry_months)
        if points > 0 and programme.expiry_months > 0
        else None
    )

    Transaction.objects.create(
        account=account,
        kind=TransactionKind.ADJUST,
        points=points,
        points_remaining=max(points, 0),
        expires_at=expires_at,
        reason=reason.strip(),
    )

    if points < 0:
        _consume(account, abs(points))
    else:
        account.lifetime_points += points
        _apply_tier(account)
        account.save(update_fields=["lifetime_points", "tier", "updated_at"])

    return Movement(points=points, balance=_balance_points(account))


def balance_of(subject: str, *, expiring_within_days: int = 30) -> Balance:
    """
    What a customer has right now.

    Summed from the ledger rather than read from a column. A stored balance
    beside a ledger is two sources of truth that agree until the first crash.
    """
    account = Account.objects.filter(subject=subject).select_related("tier").first()

    if account is None:
        return Balance(subject=subject, points=0, value=Decimal("0"), lifetime_points=0)

    points = _balance_points(account)
    horizon = timezone.now() + timedelta(days=expiring_within_days)

    expiring = (
        _live_lots(account)
        .filter(expires_at__isnull=False, expires_at__lte=horizon)
        .aggregate(total=Sum("points_remaining"))["total"]
        or 0
    )

    return Balance(
        subject=subject,
        points=points,
        value=Programme.current().value_of(points),
        lifetime_points=account.lifetime_points,
        tier_name=account.tier.name if account.tier else "",
        expiring_soon=expiring,
    )


def history(subject: str, *, limit: int = 50) -> list[dict]:
    """The ledger as a customer would read it, newest first."""
    account = Account.objects.filter(subject=subject).first()

    if account is None:
        return []

    rows = account.transactions.all()[: max(1, min(limit, 200))]

    return [
        {
            "kind": row.kind,
            "points": row.points,
            "reason": row.reason,
            "orderId": row.source_order_id,
            "createdAt": row.created_at.isoformat(),
            "expiresAt": row.expires_at.isoformat() if row.expires_at else None,
        }
        for row in rows
    ]


@transaction.atomic
def expire_stale(*, at=None) -> int:
    """
    Writes off lots whose expiry has passed. Returns the points removed.

    The worker the manifest declares. Idempotent: a lot with nothing remaining
    is skipped, so running this twice in a day costs a query and changes
    nothing.
    """
    moment = at or timezone.now()
    removed = 0

    stale = (
        Transaction.objects.select_for_update()
        .filter(
            kind__in=(TransactionKind.EARN, TransactionKind.ADJUST),
            points_remaining__gt=0,
            expires_at__isnull=False,
            expires_at__lte=moment,
        )
        .select_related("account")
    )

    for lot in stale:
        Transaction.objects.create(
            account=lot.account,
            kind=TransactionKind.EXPIRE,
            points=-lot.points_remaining,
            reason=f"Expired {lot.expires_at:%Y-%m-%d}",
            created_at=moment,
        )
        removed += lot.points_remaining
        lot.points_remaining = 0
        lot.save(update_fields=["points_remaining"])

    return removed


def ensure_default_tiers() -> int:
    """
    Creates a three-rung ladder, if none exists.

    Called from the install's configure step. A loyalty feature that installs
    with no tiers is one whose every customer sits outside the ladder, which
    looks broken rather than unconfigured.
    """
    defaults = [
        ("Member", "member", 0, Decimal("1")),
        ("Silver", "silver", 1000, Decimal("1.25")),
        ("Gold", "gold", 5000, Decimal("1.5")),
    ]

    created = 0

    for name, slug, threshold, multiplier in defaults:
        _, made = Tier.objects.get_or_create(
            slug=slug,
            defaults={"name": name, "threshold_points": threshold, "earn_multiplier": multiplier},
        )

        if made:
            created += 1

    return created


# --- Internals --------------------------------------------------------------


def _locked(subject: str) -> Account:
    """
    The account row, locked for the rest of the transaction.

    Every path that moves points goes through here. Without it, two concurrent
    redemptions both read a balance only one of them can have.
    """
    Account.objects.get_or_create(subject=subject)

    # `of=("self",)` locks the account row and nothing else. Without it Django
    # locks everything the query touches, and `tier` is nullable - so the join
    # is an outer one and PostgreSQL refuses: "FOR UPDATE cannot be applied to
    # the nullable side of an outer join". The account row is the only thing
    # that needs locking anyway; a tier is configuration, not contended state.
    return (
        Account.objects.select_for_update(of=("self",))
        .select_related("tier")
        .get(subject=subject)
    )


def _live_lots(account: Account):
    """Earn and positive-adjust rows with points still on them, oldest first."""
    return (
        Transaction.objects.filter(
            account=account,
            kind__in=(TransactionKind.EARN, TransactionKind.ADJUST),
            points_remaining__gt=0,
        )
        .exclude(expires_at__lte=timezone.now())
        .order_by("expires_at", "created_at", "id")
    )


def _balance_points(account: Account) -> int:
    """The spendable balance: what is left on unexpired lots."""
    return _live_lots(account).aggregate(total=Sum("points_remaining"))["total"] or 0


def _consume(account: Account, points: int) -> None:
    """
    Takes `points` off the oldest lots first.

    Oldest first so that what expires soonest is spent soonest, which is what a
    customer would choose and what keeps the store's liability falling rather
    than ageing.
    """
    remaining = points

    for lot in _live_lots(account).select_for_update():
        if remaining <= 0:
            break

        taken = min(lot.points_remaining, remaining)
        lot.points_remaining -= taken
        lot.save(update_fields=["points_remaining"])
        remaining -= taken


def _apply_tier(account: Account) -> None:
    """Moves the account to the highest tier its lifetime points reach."""
    account.tier = (
        Tier.objects.filter(threshold_points__lte=account.lifetime_points)
        .order_by("-threshold_points")
        .first()
    )
