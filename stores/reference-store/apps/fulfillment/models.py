"""
How this store hands goods over.

Ported from the frozen `Fulfillment` module. It was per-tenant settings there
and is a singleton here: these are settings *of the store*, and this store is the
only one ([`adr/0023`](../../../../docs/adr/0023-a-ported-store-is-single-tenant.md)).

Delivery zones and their pricing are not here. Collection-only is a complete
business, so zones are an optional Feature; what stays in the base store is the
single decision every store must make — whether people can come and collect
([`adr/0024`](../../../../docs/adr/0024-base-store-versus-optional-feature.md)).
"""

from decimal import Decimal

from django.core.exceptions import ValidationError
from django.core.validators import MinValueValidator
from django.db import models


class FulfillmentSettings(models.Model):
    """
    The store's fulfilment configuration. Exactly one row, always.

    Loaded through `current()` rather than by primary key so that a store which
    has never opened its settings page still behaves like one with defaults,
    instead of failing on a missing row somewhere deep in checkout.
    """

    id = models.PositiveSmallIntegerField(primary_key=True, default=1)

    collection_enabled = models.BooleanField(default=True)

    # Whether the store offers delivery at all. Separate from whether the
    # delivery Feature is installed: a store may have the capability and be
    # collection-only today, and turning it off must not uninstall anything.
    delivery_enabled = models.BooleanField(default=False)

    # A floor beneath which the store will not deliver. Zero means none.
    delivery_minimum_order = models.DecimalField(
        max_digits=12,
        decimal_places=2,
        default=Decimal("0"),
        validators=[MinValueValidator(Decimal("0"))],
    )

    # Typical preparation time, shown to shoppers as a promise. Held here rather
    # than per product because a kitchen has one queue.
    preparation_minutes = models.PositiveIntegerField(default=20)

    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        verbose_name_plural = "fulfillment settings"

    def clean(self) -> None:
        super().clean()

        if not self.collection_enabled and not self.delivery_enabled:
            # A store offering neither cannot take an order at all. Refused here
            # rather than discovered at checkout, where it would read as a bug.
            raise ValidationError(
                "A store must offer collection, delivery, or both — otherwise it cannot sell anything."
            )

    @classmethod
    def current(cls) -> "FulfillmentSettings":
        """The store's settings, created with defaults the first time they are asked for."""
        return cls.objects.get_or_create(id=1)[0]

    def __str__(self) -> str:
        offered = [
            name
            for name, enabled in (("collection", self.collection_enabled), ("delivery", self.delivery_enabled))
            if enabled
        ]

        return ", ".join(offered) or "nothing"
