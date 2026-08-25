"""
How this store hands goods over, and what it charges to do so.

Ported from the frozen `Fulfillment` module. It was per-tenant settings there
and is a singleton here: these are settings *of the store*, and this store is the
only one ([`adr/0023`](../../../../docs/adr/0023-a-ported-store-is-single-tenant.md)).

Delivery zones and their pricing live here too. They were an optional Feature on
the argument that collection-only is a complete business; the catalogue revision
overruled that, because a shop that cannot charge differently for the next town
than for the next street is missing table stakes rather than running a plainer
business ([`adr/0024`](../../../../docs/adr/0024-base-store-versus-optional-feature.md)).

Orders record the zone they were delivered to as a snapshot rather than a
foreign key, so a completed order stays explicable whatever happens to the zone
afterwards — archived, renamed, or moved into this app from the Feature that
used to own it.
"""

from decimal import Decimal

from django.core.exceptions import ValidationError
from django.core.validators import MinValueValidator
from django.db import models
from django.utils import timezone


class ZoneStatus(models.TextChoices):
    ACTIVE = "Active", "Active"
    ARCHIVED = "Archived", "Archived"


class FulfillmentSettings(models.Model):
    """
    The store's fulfilment configuration. Exactly one row, always.

    Loaded through `current()` rather than by primary key so that a store which
    has never opened its settings page still behaves like one with defaults,
    instead of failing on a missing row somewhere deep in checkout.
    """

    id = models.PositiveSmallIntegerField(primary_key=True, default=1)

    collection_enabled = models.BooleanField(default=True)

    # Whether the store offers delivery at all — a standing commercial decision.
    delivery_enabled = models.BooleanField(default=False)

    # A pause switch, and deliberately not the same field as the one above. A
    # kitchen stopping deliveries for an hour should not have to reconfigure its
    # zones, and turning this back on must restore exactly what was there before.
    delivery_accepting_orders = models.BooleanField(default=True)

    # A floor beneath which the store will not deliver. Zero means none. A zone
    # may set its own, which overrides this rather than adding to it.
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

    @property
    def default_minimum_order(self) -> Decimal | None:
        """
        The store-wide delivery minimum, or None where there is none.

        Zero and "no minimum" are the same commercial fact and a different
        arithmetic one, and every caller wants the second reading — so the
        conversion happens once, here.
        """
        return self.delivery_minimum_order if self.delivery_minimum_order > Decimal("0") else None

    def __str__(self) -> str:
        offered = [
            name
            for name, enabled in (("collection", self.collection_enabled), ("delivery", self.delivery_enabled))
            if enabled
        ]

        return ", ".join(offered) or "nothing"


class DeliveryZone(models.Model):
    """
    An area the store delivers to, and what it charges for it.

    A zone's own minimum overrides the store default rather than adding to it:
    a far suburb that only makes sense above a larger basket is exactly the case
    this exists for, and two figures that combined would be impossible to explain
    to a shopper.
    """

    name = models.CharField(max_length=150)
    fee = models.DecimalField(
        max_digits=12, decimal_places=2, validators=[MinValueValidator(Decimal("0"))]
    )
    minimum_order_subtotal = models.DecimalField(
        max_digits=12, decimal_places=2, null=True, blank=True,
        validators=[MinValueValidator(Decimal("0"))],
    )
    status = models.CharField(max_length=20, choices=ZoneStatus, default=ZoneStatus.ACTIVE)
    display_order = models.PositiveIntegerField(default=0)
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    archived_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        ordering = ("display_order", "name")
        constraints = [
            models.UniqueConstraint(
                fields=["name"],
                condition=models.Q(status="Active"),
                # Namespaced away from the delivery-zones Feature's constraint
                # of the same purpose: both tables exist at once while a store
                # is being migrated off it.
                name="fulfillment_zone_active_name_unique",
            ),
        ]

    def clean(self) -> None:
        super().clean()

        if not self.name.strip():
            raise ValidationError({"name": "A zone needs a name a shopper will recognise."})

    def minimum_for(self, settings: FulfillmentSettings | None = None) -> Decimal | None:
        """
        The minimum basket for this zone: its own where set, the store default
        otherwise, and none when neither is.
        """
        if self.minimum_order_subtotal is not None:
            return self.minimum_order_subtotal

        return (settings or FulfillmentSettings.current()).default_minimum_order

    def accepts(self, subtotal: Decimal, settings: FulfillmentSettings | None = None) -> bool:
        """Whether an order of this size can be delivered to this zone right now."""
        resolved = settings or FulfillmentSettings.current()

        if self.status != ZoneStatus.ACTIVE:
            return False

        # Both switches, and they mean different things: a store that does not
        # deliver at all, and one that has stopped for the evening.
        if not resolved.delivery_enabled or not resolved.delivery_accepting_orders:
            return False

        minimum = self.minimum_for(resolved)

        return minimum is None or subtotal >= minimum

    def archive(self) -> None:
        """
        Withdraws the zone without deleting it.

        Orders name it, and the partial unique constraint means the name becomes
        free for a new zone — a business reorganising its areas should not have
        to invent a name it has already used.
        """
        self.status = ZoneStatus.ARCHIVED
        self.archived_at = timezone.now()

    def __str__(self) -> str:
        return self.name
