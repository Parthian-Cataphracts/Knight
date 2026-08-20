"""
People who buy from this store.

Ported from the frozen .NET `Customer` module and deliberately renamed. In
KNIGHT's vocabulary a *customer* is the business that buys KNIGHT; the person
buying a sandwich from one of their stores is a different party entirely, and
one word meaning both across two codebases is how somebody eventually writes the
wrong query ([`adr/0023`](../../../../docs/adr/0023-a-ported-store-is-single-tenant.md)).

A shopper is identified by phone number, not email. That is the .NET module's
choice and it is the right one for this market: a phone number is what somebody
gives at a counter, what a delivery driver calls, and what a returning shopper
remembers.
"""

import re

from django.core.exceptions import ValidationError
from django.db import models


class ShopperStatus(models.TextChoices):
    ACTIVE = "Active", "Active"
    BLOCKED = "Blocked", "Blocked"


def normalize_phone(value: str | None) -> str:
    """
    Reduces a phone number to the digits that identify it.

    Separators, spaces and a leading +98 or 0098 all disappear, so the same
    person typing 0912 345 6789 and +989123456789 is one shopper rather than
    two. Uniqueness is declared on the normalised value, which makes this
    function the definition of "the same person" rather than a convenience
    beside it.
    """
    if value is None or not value.strip():
        raise ValidationError("A phone number is required.")

    digits = re.sub(r"[^\d]", "", value.strip())

    # Iranian numbers arrive in three shapes for the same subscriber. Reduced to
    # the national form so all three match.
    if digits.startswith("0098"):
        digits = "0" + digits[4:]
    elif digits.startswith("98") and len(digits) > 10:
        digits = "0" + digits[2:]

    if len(digits) < 10:
        raise ValidationError("That does not look like a phone number.")

    return digits


class Shopper(models.Model):
    """
    Somebody who has ordered, or been recorded ahead of ordering.

    Orders do not point at this row. They carry a snapshot of who ordered, so
    that a shopper renaming themselves or being deleted cannot rewrite the
    history of what happened — see `orders.OrderParty`.
    """

    display_name = models.CharField(max_length=200)
    phone = models.CharField(max_length=32)

    # What matching is done on. Not editable: it is derived, and letting somebody
    # set it by hand would let two shoppers claim the same identity.
    normalized_phone = models.CharField(max_length=32, unique=True, editable=False)

    email = models.EmailField(blank=True, default="")
    status = models.CharField(max_length=20, choices=ShopperStatus, default=ShopperStatus.ACTIVE)
    notes = models.TextField(max_length=2000, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        ordering = ("display_name",)
        indexes = [models.Index(fields=["status"])]

    @property
    def can_order(self) -> bool:
        return self.status == ShopperStatus.ACTIVE

    def block(self, reason: str = "") -> None:
        """
        Stops this shopper ordering, without deleting them.

        Their order history is the reason they were blocked and the reason a
        dispute can be settled later, so the row stays.
        """
        self.status = ShopperStatus.BLOCKED

        if reason:
            self.notes = f"{self.notes}\n{reason}".strip()

    def save(self, *args, **kwargs):
        self.normalized_phone = normalize_phone(self.phone)

        return super().save(*args, **kwargs)

    def __str__(self) -> str:
        return f"{self.display_name} ({self.phone})"
