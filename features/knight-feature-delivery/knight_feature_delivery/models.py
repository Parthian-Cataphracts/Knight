"""
Delivery zones and what they cost.

Ported from the frozen .NET `Delivery` module and shipped as an optional Feature.
Collection-only is a complete business, so zone pricing is an upgrade rather
than a requirement ([`adr/0024`](../../../docs/adr/0024-base-store-versus-optional-feature.md)).

The base store already knows *whether* it delivers — that is one switch in
`apps.fulfillment`. What lives here is the part a store only needs once delivery
is worth pricing properly: named areas, a fee each, and a minimum order per area.

Orders record the zone they were delivered to as a snapshot, so a completed
order stays explicable after this Feature is uninstalled and these tables are
gone.
"""

from decimal import Decimal

from django.core.exceptions import ValidationError
from django.core.validators import MinValueValidator
from django.db import models
from django.utils import timezone


class ZoneStatus(models.TextChoices):
    ACTIVE = "Active", "Active"
    ARCHIVED = "Archived", "Archived"


class DeliverySettings(models.Model):
    """
    Store-wide delivery configuration owned by this Feature.

    Deliberately separate from `apps.fulfillment.FulfillmentSettings`, which stays
    in the base store. That one answers "does this store deliver at all"; this one
    answers "on what terms" — and only the second disappears when the Feature is
    uninstalled.
    """

    id = models.PositiveSmallIntegerField(primary_key=True, default=1)

    # A pause switch that is not the same as turning delivery off. A kitchen
    # stopping deliveries for an hour should not have to reconfigure its zones,
    # and turning this back on must restore exactly what was there before.
    is_accepting_orders = models.BooleanField(default=True)

    default_minimum_order = models.DecimalField(
        max_digits=12, decimal_places=2, null=True, blank=True,
        validators=[MinValueValidator(Decimal("0"))],
    )

    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        verbose_name_plural = "delivery settings"

    @classmethod
    def current(cls) -> "DeliverySettings":
        """The store's settings, created with defaults the first time they are asked for."""
        return cls.objects.get_or_create(id=1)[0]

    def __str__(self) -> str:
        return "accepting" if self.is_accepting_orders else "paused"


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
                name="delivery_zone_active_name_unique",
            ),
        ]

    def clean(self) -> None:
        super().clean()

        if not self.name.strip():
            raise ValidationError({"name": "A zone needs a name a shopper will recognise."})

    def minimum_for(self, settings: DeliverySettings | None = None) -> Decimal | None:
        """
        The minimum basket for this zone: its own where set, the store default
        otherwise, and none when neither is.
        """
        if self.minimum_order_subtotal is not None:
            return self.minimum_order_subtotal

        return (settings or DeliverySettings.current()).default_minimum_order

    def accepts(self, subtotal: Decimal, settings: DeliverySettings | None = None) -> bool:
        """Whether an order of this size can be delivered to this zone right now."""
        resolved = settings or DeliverySettings.current()

        if self.status != ZoneStatus.ACTIVE or not resolved.is_accepting_orders:
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
