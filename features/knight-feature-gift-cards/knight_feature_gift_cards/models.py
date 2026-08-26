"""
Gift cards and store credit, and the ledger both are made of.

This is money. Not a points balance that a merchant can be generous about — a
gift card is a bearer instrument somebody paid real currency for, and store
credit is a debt the shop owes. Every decision here is the conservative one.

**The ledger is the truth and every balance is derived from it.** The same rule
as `loyalty-rewards`, and it matters more here: a cached balance that drifts on a
points programme is an apology, and on a gift card it is either theft from the
customer or a loss for the shop.

**Amounts are `Decimal`, never float.** A tenth of a cent lost to binary rounding
is a rounding error in a spreadsheet and a reconciliation failure in a business.

**A code is a secret.** It is generated from a cryptographic source over an
alphabet with no ambiguous characters, because a guessable gift-card code is a
way to spend somebody else's money and an ambiguous one is a support call.
"""

import secrets
from decimal import Decimal

from django.core.exceptions import ValidationError
from django.core.validators import MinValueValidator
from django.db import models
from django.utils import timezone

#: No O/0, no I/1/L, no U (it is read as V on a printed card). What is left is
#: unambiguous read aloud, over the phone, and off a receipt.
CODE_ALPHABET = "ACDEFGHJKMNPQRTVWXYZ2346789"

#: 16 characters over a 27-letter alphabet is about 76 bits. A gift card is a
#: bearer instrument: the code *is* the authorisation, so guessing one must be
#: infeasible rather than merely unlikely.
CODE_LENGTH = 16


def generate_code() -> str:
    """A fresh code, in groups of four for a human to read back."""
    raw = "".join(secrets.choice(CODE_ALPHABET) for _ in range(CODE_LENGTH))

    return "-".join(raw[index : index + 4] for index in range(0, CODE_LENGTH, 4))


def normalize_code(value: str | None) -> str:
    """
    Reduces a code to what it is matched by.

    Shoppers type codes with the groups, without them, in either case, and with
    stray spaces. Uniqueness is declared on the normalised value, which makes
    this the definition of sameness rather than a convenience beside it.
    """
    if value is None or not value.strip():
        raise ValidationError("A gift card needs a code.")

    return "".join(character for character in value.upper() if character.isalnum())


class CardStatus(models.TextChoices):
    ACTIVE = "Active", "Active"
    # Spent down to nothing. Kept distinct from Void so a shopper who asks why
    # their card stopped working gets the true answer.
    DEPLETED = "Depleted", "Fully redeemed"
    VOID = "Void", "Voided"


class EntryKind(models.TextChoices):
    """
    Why the ledger moved. Signed amounts, so a balance is a sum.

    `ISSUE` is positive and happens once. `REFUND` is positive and is what puts
    value back when an order that spent the card is cancelled. `VOID` is negative
    and writes the remaining value off.
    """

    ISSUE = "Issue", "Issued"
    REDEEM = "Redeem", "Redeemed"
    REFUND = "Refund", "Refunded"
    VOID = "Void", "Voided"


class GiftCard(models.Model):
    """
    One card. Carries its face value and no balance.

    `initial_amount` is what it was sold for and never changes. What is left is
    the sum of the ledger, which is the only number that can be trusted after a
    crash halfway through a redemption.
    """

    code = models.CharField(max_length=32)
    normalized_code = models.CharField(max_length=32, unique=True, editable=False)

    initial_amount = models.DecimalField(
        max_digits=12, decimal_places=2, validators=[MinValueValidator(Decimal("0.01"))]
    )

    # Stored on the card rather than assumed from the store's settings. A card
    # sold in one currency cannot be spent in another, and a store that changes
    # its currency must not silently revalue every outstanding card.
    currency = models.CharField(max_length=3, default="EUR")

    status = models.CharField(max_length=16, choices=CardStatus, default=CardStatus.ACTIVE)

    # Who it was bought for, and by whom. Free text: a gift card is often bought
    # for somebody who has no account at the shop at all.
    recipient_email = models.EmailField(blank=True, default="")
    sender_name = models.CharField(max_length=150, blank=True, default="")
    message = models.TextField(max_length=1000, blank=True, default="")

    # The order that bought the card, as a plain id. A foreign key would be a
    # database-level coupling into the base store.
    source_order_id = models.BigIntegerField(null=True, blank=True)

    issued_at = models.DateTimeField(default=timezone.now)

    # Null means it never expires, which is the law in several places and the
    # right default everywhere else.
    expires_at = models.DateTimeField(null=True, blank=True)

    voided_at = models.DateTimeField(null=True, blank=True)
    void_reason = models.CharField(max_length=250, blank=True, default="")

    class Meta:
        db_table = "knight_gift_card"
        ordering = ("-issued_at", "-id")
        indexes = [
            models.Index(fields=["status", "-issued_at"], name="knight_gc_status_idx"),
        ]

    def save(self, *args, **kwargs):
        if not self.code:
            self.code = generate_code()

        self.normalized_code = normalize_code(self.code)

        return super().save(*args, **kwargs)

    def clean(self) -> None:
        super().clean()

        if self.expires_at and self.expires_at <= self.issued_at:
            raise ValidationError({"expires_at": "A card cannot expire before it is issued."})

    def has_expired(self, at=None) -> bool:
        return self.expires_at is not None and (at or timezone.now()) >= self.expires_at

    def is_redeemable(self, at=None) -> bool:
        """
        Whether this card may be spent at all — before any question of amount.

        `DEPLETED` counts as redeemable here, which looks odd and is deliberate.
        Depletion is a statement about the balance, and the balance is the
        ledger's business; a refund can put value back on a depleted card and
        this method must not be the thing that then refuses it. Only a void card
        and an expired one are unspendable in principle.

        Answering "no" for two different reasons in one method is how a shopper
        with an empty card gets told it was voided.
        """
        return self.status != CardStatus.VOID and not self.has_expired(at)

    def __str__(self) -> str:
        return f"{self.code} ({self.initial_amount} {self.currency})"


class GiftCardEntry(models.Model):
    """
    One movement on one card. Append-only — nothing here is ever updated.

    Unique per card and order for each kind, which is what makes redeeming
    idempotent: a retried checkout cannot spend the same card twice for the same
    order, and the database is the only place that race can be settled.
    """

    card = models.ForeignKey(GiftCard, on_delete=models.CASCADE, related_name="entries")
    kind = models.CharField(max_length=16, choices=EntryKind)
    amount = models.DecimalField(max_digits=12, decimal_places=2)

    source_order_id = models.BigIntegerField(null=True, blank=True)
    reason = models.CharField(max_length=250, blank=True, default="")
    created_at = models.DateTimeField(default=timezone.now)

    class Meta:
        db_table = "knight_gift_card_entry"
        ordering = ("created_at", "id")
        indexes = [
            models.Index(fields=["card", "created_at"], name="knight_gc_entry_card_idx"),
        ]
        constraints = [
            models.UniqueConstraint(
                fields=["card", "kind", "source_order_id"],
                condition=models.Q(source_order_id__isnull=False),
                name="knight_gc_once_per_card_order_and_kind",
            ),
            models.CheckConstraint(
                condition=~models.Q(amount=Decimal("0")),
                name="knight_gc_no_zero_movements",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.kind} {self.amount:+} on card {self.card_id}"


class CreditEntry(models.Model):
    """
    Store credit, as a ledger keyed on a customer rather than a card.

    A separate table from `GiftCardEntry` rather than a nullable card column on
    one. They are different instruments: a card is a bearer token that anybody
    holding the code may spend, and credit belongs to one customer and cannot be
    transferred. Sharing a table would mean every query having to remember which
    it was looking at.

    The customer is an opaque **subject string**, the same one the store passes
    elsewhere. This package cannot see the shopper table.
    """

    subject = models.CharField(max_length=200)
    kind = models.CharField(max_length=16, choices=EntryKind)
    amount = models.DecimalField(max_digits=12, decimal_places=2)
    currency = models.CharField(max_length=3, default="EUR")

    source_order_id = models.BigIntegerField(null=True, blank=True)
    reason = models.CharField(max_length=250, blank=True, default="")
    created_at = models.DateTimeField(default=timezone.now)

    class Meta:
        db_table = "knight_store_credit_entry"
        ordering = ("created_at", "id")
        indexes = [
            models.Index(fields=["subject", "created_at"], name="knight_credit_subject_idx"),
        ]
        constraints = [
            models.UniqueConstraint(
                fields=["subject", "kind", "source_order_id"],
                condition=models.Q(source_order_id__isnull=False),
                name="knight_credit_once_per_subject_order_and_kind",
            ),
            models.CheckConstraint(
                condition=~models.Q(amount=Decimal("0")),
                name="knight_credit_no_zero_movements",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.kind} {self.amount:+} for {self.subject}"
