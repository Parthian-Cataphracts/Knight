"""
Issuing, redeeming and refunding gift cards and store credit.

The surface the store's checkout calls. Every path that moves value locks the
card (or the customer's credit rows) for the rest of the transaction, because
the ways a money ledger goes wrong are all races: one order spending a card
twice, two orders both spending the last of it, and a retried checkout
double-charging.

Idempotency is by **constraint**, never by checking first. Two concurrent
checkouts both reading "not yet redeemed" is exactly how a card is spent twice.

Nothing here ever lets a balance go negative. A gift card that can be
overdrawn is a shop giving away money it was never paid.
"""

from __future__ import annotations

from dataclasses import dataclass
from decimal import Decimal

from django.db import IntegrityError, transaction
from django.db.models import Sum
from django.utils import timezone

from .models import (
    CardStatus,
    CreditEntry,
    EntryKind,
    GiftCard,
    GiftCardEntry,
    normalize_code,
)

ZERO = Decimal("0.00")


class GiftCardError(RuntimeError):
    """
    A redemption that cannot be honoured, with a reason a shopper can read.

    Deliberately one exception type with a message rather than a type per
    failure. The caller's job is to show the shopper why, not to branch: "this
    card has expired" and "this card has 3.00 left" lead to the same screen.
    """


@dataclass(frozen=True)
class CardBalance:
    """What is left on a card, and whether it can be spent."""

    code: str
    currency: str
    initial: Decimal
    remaining: Decimal
    status: str
    redeemable: bool
    expires_at: object = None

    @property
    def is_depleted(self) -> bool:
        return self.remaining <= ZERO


@dataclass(frozen=True)
class Redemption:
    """
    What a redemption actually took.

    `applied` is the amount that moved, which may legitimately be less than what
    was asked for — a card with 5.00 left against a 20.00 order pays 5.00, and
    the checkout takes the rest another way. That is partial *settlement*, not a
    partial failure, and it is the normal case for a gift card.
    """

    applied: Decimal
    remaining: Decimal
    duplicate: bool = False

    @property
    def moved(self) -> bool:
        return self.applied > ZERO and not self.duplicate


def _money(value) -> Decimal:
    """Everything that touches a balance goes through here. Never a float."""
    return Decimal(str(value)).quantize(Decimal("0.01"))


# --- Gift cards -------------------------------------------------------------


@transaction.atomic
def issue(
    amount,
    *,
    currency: str = "EUR",
    recipient_email: str = "",
    sender_name: str = "",
    message: str = "",
    source_order_id: int | None = None,
    expires_at=None,
) -> GiftCard:
    """
    Sells a card and writes its opening entry.

    The card and its `ISSUE` entry are created together or not at all. A card
    row without an opening entry would have a zero balance and look depleted the
    moment it was handed over.
    """
    value = _money(amount)

    if value <= ZERO:
        raise GiftCardError("A gift card needs a positive amount.")

    card = GiftCard(
        initial_amount=value,
        currency=currency,
        recipient_email=recipient_email,
        sender_name=sender_name,
        message=message,
        source_order_id=source_order_id,
        expires_at=expires_at,
    )
    card.full_clean(exclude=["normalized_code", "code"])
    card.save()

    GiftCardEntry.objects.create(
        card=card,
        kind=EntryKind.ISSUE,
        amount=value,
        source_order_id=source_order_id,
        reason="Issued",
    )

    return card


def find(code: str) -> GiftCard | None:
    """The card a shopper's code refers to, however they typed it."""
    try:
        normalized = normalize_code(code)
    except Exception:  # noqa: BLE001 - a blank code is a miss, not a crash
        return None

    return GiftCard.objects.filter(normalized_code=normalized).first()


def balance(code: str) -> CardBalance | None:
    """
    What is left on a card. None when there is no such card.

    None rather than a zero balance for an unknown code: "this card does not
    exist" and "this card is empty" are different answers, and a shopper who
    mistyped needs the first one.
    """
    card = find(code)

    if card is None:
        return None

    return _describe(card)


@transaction.atomic
def redeem(code: str, amount, *, source_order_id: int, reason: str = "") -> Redemption:
    """
    Spends up to `amount` from a card.

    Takes the lesser of what was asked and what is left, because that is what a
    gift card is for: a card with 5.00 against a 20.00 basket pays 5.00 and the
    checkout collects 15.00 some other way. Refusing outright would make every
    partly-spent card unusable.

    Refuses only when the card cannot be spent at all — unknown, expired, voided,
    empty, or the wrong currency.
    """
    card = _locked(code)
    state = _describe(card)

    if not card.is_redeemable():
        raise GiftCardError(
            "This gift card has expired." if card.has_expired() else "This gift card is not active."
        )

    if state.remaining <= ZERO:
        raise GiftCardError("This gift card has no value left on it.")

    wanted = _money(amount)

    if wanted <= ZERO:
        raise GiftCardError("A redemption needs a positive amount.")

    applied = min(wanted, state.remaining)

    try:
        # Its own savepoint. An IntegrityError marks the whole transaction broken
        # in PostgreSQL, so without this the duplicate branch below could not run
        # a query — and a retried checkout would fail instead of being told it
        # had already been settled.
        with transaction.atomic():
            GiftCardEntry.objects.create(
                card=card,
                kind=EntryKind.REDEEM,
                amount=-applied,
                source_order_id=source_order_id,
                reason=reason,
            )
    except IntegrityError:
        already = _remaining(card)

        return Redemption(applied=ZERO, remaining=already, duplicate=True)

    remaining = _remaining(card)

    if remaining <= ZERO and card.status == CardStatus.ACTIVE:
        # Recorded so a shopper is told "fully redeemed" rather than "not
        # active", which is what a void would say.
        card.status = CardStatus.DEPLETED
        card.save(update_fields=["status"])

    return Redemption(applied=applied, remaining=remaining)


@transaction.atomic
def refund(code: str, *, source_order_id: int, reason: str = "") -> Redemption:
    """
    Puts back what an order took, when that order is cancelled.

    Written as a new entry rather than by deleting the redemption. A money ledger
    that can be edited answers no question worth asking, and "why does this card
    have this balance" has to stay answerable after a refund.
    """
    card = _locked(code)

    spent = GiftCardEntry.objects.filter(
        card=card, kind=EntryKind.REDEEM, source_order_id=source_order_id
    ).first()

    if spent is None:
        return Redemption(applied=ZERO, remaining=_remaining(card))

    returned = abs(spent.amount)

    try:
        with transaction.atomic():
            GiftCardEntry.objects.create(
                card=card,
                kind=EntryKind.REFUND,
                amount=returned,
                source_order_id=source_order_id,
                reason=reason or f"Order {source_order_id} cancelled",
            )
    except IntegrityError:
        return Redemption(applied=ZERO, remaining=_remaining(card), duplicate=True)

    if card.status == CardStatus.DEPLETED:
        # It has value again, so it is spendable again.
        card.status = CardStatus.ACTIVE
        card.save(update_fields=["status"])

    return Redemption(applied=returned, remaining=_remaining(card))


@transaction.atomic
def void(code: str, *, reason: str) -> CardBalance:
    """
    Writes a card off — bought fraudulently, reported stolen, issued in error.

    The remaining value is written off as a ledger entry rather than by setting
    the balance to zero, so the write-off is as auditable as the sale was.
    """
    if not reason.strip():
        raise GiftCardError("Voiding a card needs a reason.")

    card = _locked(code)

    if card.status == CardStatus.VOID:
        return _describe(card)

    remaining = _remaining(card)

    if remaining > ZERO:
        GiftCardEntry.objects.create(
            card=card, kind=EntryKind.VOID, amount=-remaining, reason=reason.strip()
        )

    card.status = CardStatus.VOID
    card.voided_at = timezone.now()
    card.void_reason = reason.strip()[:250]
    card.save(update_fields=["status", "voided_at", "void_reason"])

    return _describe(card)


def history(code: str) -> list[dict]:
    """The card's ledger, oldest first, as a support agent would read it."""
    card = find(code)

    if card is None:
        return []

    return [
        {
            "kind": entry.kind,
            "amount": str(entry.amount),
            "orderId": entry.source_order_id,
            "reason": entry.reason,
            "createdAt": entry.created_at.isoformat(),
        }
        for entry in card.entries.all()
    ]


def outstanding(currency: str | None = None) -> Decimal:
    """
    What the shop owes on unspent cards.

    The number an accountant asks for, and the reason this Feature keeps a
    ledger rather than a counter: it is a liability, and it has to be summable
    across every card at a point in time.
    """
    entries = GiftCardEntry.objects.exclude(card__status=CardStatus.VOID)

    if currency is not None:
        entries = entries.filter(card__currency=currency)

    return _money(entries.aggregate(total=Sum("amount"))["total"] or ZERO)


# --- Store credit -----------------------------------------------------------


@transaction.atomic
def grant_credit(
    subject: str,
    amount,
    *,
    currency: str = "EUR",
    reason: str,
    source_order_id: int | None = None,
) -> Decimal:
    """
    Gives a customer store credit. Returns the new balance.

    A reason is required. Credit is a debt the shop takes on, and one that
    appeared with no explanation is one nobody can reconcile.
    """
    value = _money(amount)

    if value <= ZERO:
        raise GiftCardError("A credit grant needs a positive amount.")

    if not reason.strip():
        raise GiftCardError("A credit grant needs a reason.")

    CreditEntry.objects.create(
        subject=subject,
        kind=EntryKind.ISSUE,
        amount=value,
        currency=currency,
        reason=reason.strip(),
        source_order_id=source_order_id,
    )

    return credit_balance(subject, currency=currency)


@transaction.atomic
def spend_credit(
    subject: str, amount, *, source_order_id: int, currency: str = "EUR", reason: str = ""
) -> Redemption:
    """
    Spends up to `amount` of a customer's credit.

    Partial settlement, for the same reason as a gift card: credit of 5.00
    against a 20.00 basket pays 5.00.
    """
    # Lock the customer's rows for the rest of the transaction. Without this,
    # two concurrent checkouts both see a balance only one of them can spend.
    list(
        CreditEntry.objects.select_for_update()
        .filter(subject=subject, currency=currency)
        .values_list("id", flat=True)
    )

    available = credit_balance(subject, currency=currency)

    if available <= ZERO:
        raise GiftCardError("There is no store credit on this account.")

    wanted = _money(amount)

    if wanted <= ZERO:
        raise GiftCardError("A redemption needs a positive amount.")

    applied = min(wanted, available)

    try:
        with transaction.atomic():
            CreditEntry.objects.create(
                subject=subject,
                kind=EntryKind.REDEEM,
                amount=-applied,
                currency=currency,
                source_order_id=source_order_id,
                reason=reason,
            )
    except IntegrityError:
        return Redemption(
            applied=ZERO, remaining=credit_balance(subject, currency=currency), duplicate=True
        )

    return Redemption(applied=applied, remaining=credit_balance(subject, currency=currency))


@transaction.atomic
def refund_credit(
    subject: str, *, source_order_id: int, currency: str = "EUR", reason: str = ""
) -> Redemption:
    """Puts back credit an order spent, when that order is cancelled."""
    spent = CreditEntry.objects.filter(
        subject=subject,
        kind=EntryKind.REDEEM,
        source_order_id=source_order_id,
        currency=currency,
    ).first()

    if spent is None:
        return Redemption(applied=ZERO, remaining=credit_balance(subject, currency=currency))

    returned = abs(spent.amount)

    try:
        with transaction.atomic():
            CreditEntry.objects.create(
                subject=subject,
                kind=EntryKind.REFUND,
                amount=returned,
                currency=currency,
                source_order_id=source_order_id,
                reason=reason or f"Order {source_order_id} cancelled",
            )
    except IntegrityError:
        return Redemption(
            applied=ZERO, remaining=credit_balance(subject, currency=currency), duplicate=True
        )

    return Redemption(applied=returned, remaining=credit_balance(subject, currency=currency))


def credit_balance(subject: str, *, currency: str = "EUR") -> Decimal:
    """A customer's store credit, summed from the ledger."""
    total = CreditEntry.objects.filter(subject=subject, currency=currency).aggregate(
        total=Sum("amount")
    )["total"]

    return _money(total or ZERO)


def credit_history(subject: str, *, currency: str = "EUR") -> list[dict]:
    """The credit ledger for one customer, oldest first."""
    return [
        {
            "kind": entry.kind,
            "amount": str(entry.amount),
            "orderId": entry.source_order_id,
            "reason": entry.reason,
            "createdAt": entry.created_at.isoformat(),
        }
        for entry in CreditEntry.objects.filter(subject=subject, currency=currency)
    ]


# --- Internals --------------------------------------------------------------


def _locked(code: str) -> GiftCard:
    """
    The card row, locked for the rest of the transaction.

    Every path that moves value goes through here. `of=("self",)` locks the card
    and nothing joined to it.
    """
    card = find(code)

    if card is None:
        raise GiftCardError("No gift card matches that code.")

    return GiftCard.objects.select_for_update(of=("self",)).get(pk=card.pk)


def _remaining(card: GiftCard) -> Decimal:
    """
    What is left, summed from the ledger.

    Derived rather than stored. A cached balance beside a ledger is two sources
    of truth that agree until the first crash, and on a gift card the one a
    customer argues about is either theft from them or a loss for the shop.
    """
    total = card.entries.aggregate(total=Sum("amount"))["total"]

    return _money(total or ZERO)


def _describe(card: GiftCard) -> CardBalance:
    remaining = _remaining(card)

    return CardBalance(
        code=card.code,
        currency=card.currency,
        initial=_money(card.initial_amount),
        remaining=remaining,
        status=card.status,
        redeemable=card.is_redeemable() and remaining > ZERO,
        expires_at=card.expires_at,
    )
