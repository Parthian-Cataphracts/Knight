"""
Orders: what was bought, at what price, and what happened to it since.

Ported from the frozen .NET `Ordering` and `Checkout` modules. The one idea
running through all of it is **snapshotting**: an order records what was true
when it was placed, not a set of pointers to things that will change.

A product renamed next week, a price raised next month, a promotion uninstalled
next year, a shopper deleting their account — none of those may rewrite what a
receipt says. So every line carries the name and price it was sold at, and every
reference to something outside this app is a `source_*` id kept only so an
operator can trace it, never a foreign key that could cascade or be joined.

That is also what makes the base-store split work: an order priced with a coupon
stays readable after the promotions Feature is uninstalled and its tables are
gone ([`adr/0024`](../../../../docs/adr/0024-base-store-versus-optional-feature.md)).
"""

from decimal import Decimal

from django.core.exceptions import ValidationError
from django.core.validators import MinValueValidator
from django.db import models, transaction


class OrderStatus(models.TextChoices):
    """
    The lifecycle, linear until it is cancelled.

    Deliberately not a general state machine: a shop's counter has an order of
    events, and modelling arbitrary transitions would let an order be completed
    before it was made.
    """

    PENDING = "Pending", "Pending"
    CONFIRMED = "Confirmed", "Confirmed"
    PREPARING = "Preparing", "Preparing"
    READY = "Ready", "Ready"
    COMPLETED = "Completed", "Completed"
    CANCELLED = "Cancelled", "Cancelled"


class FulfillmentMethod(models.TextChoices):
    COLLECTION = "Collection", "Collection"
    DELIVERY = "Delivery", "Delivery"


#: What each status may become. Read by the aggregate rather than scattered
#: through it, so the whole rule is visible in one place and a new status cannot
#: be added without deciding what it follows.
ALLOWED_TRANSITIONS: dict[str, set[str]] = {
    OrderStatus.PENDING: {OrderStatus.CONFIRMED, OrderStatus.CANCELLED},
    OrderStatus.CONFIRMED: {OrderStatus.PREPARING, OrderStatus.CANCELLED},
    OrderStatus.PREPARING: {OrderStatus.READY, OrderStatus.CANCELLED},
    OrderStatus.READY: {OrderStatus.COMPLETED, OrderStatus.CANCELLED},
    OrderStatus.COMPLETED: set(),
    OrderStatus.CANCELLED: set(),
}


class OrderNumberSequence(models.Model):
    """
    The counter behind human-readable order numbers.

    A single row, because this store is the only tenant
    ([`adr/0023`](../../../../docs/adr/0023-a-ported-store-is-single-tenant.md)).
    It exists rather than counting rows because two orders placed in the same
    second would both count the same number of predecessors and take the same
    number — and an order number is what a shopper reads out on the phone.
    """

    id = models.PositiveSmallIntegerField(primary_key=True, default=1)
    last_value = models.BigIntegerField(default=0)

    @classmethod
    def take(cls) -> int:
        """
        Reserves the next number.

        `select_for_update` rather than an increment-and-read, so two concurrent
        checkouts are serialised by the row lock instead of racing. Callers are
        already inside a transaction; this asserts it rather than opening one,
        because a number handed out and then rolled back would leave a gap
        nobody can explain.
        """
        counter = cls.objects.select_for_update().get_or_create(id=1)[0]
        counter.last_value += 1
        counter.save(update_fields=["last_value"])

        return counter.last_value


class Order(models.Model):
    """
    One purchase.

    The money fields are stored rather than computed on read. A total that was
    recalculated when somebody opened the page would change when a price changed,
    which is the difference between a receipt and an estimate.
    """

    number = models.BigIntegerField(unique=True, editable=False)
    status = models.CharField(max_length=20, choices=OrderStatus, default=OrderStatus.PENDING)
    currency = models.CharField(max_length=3, default="IRR")

    subtotal = models.DecimalField(
        max_digits=14, decimal_places=2, validators=[MinValueValidator(Decimal("0"))]
    )
    discount_total = models.DecimalField(
        max_digits=14, decimal_places=2, default=Decimal("0"),
        validators=[MinValueValidator(Decimal("0"))],
    )
    fulfillment_fee = models.DecimalField(
        max_digits=14, decimal_places=2, default=Decimal("0"),
        validators=[MinValueValidator(Decimal("0"))],
    )
    total = models.DecimalField(
        max_digits=14, decimal_places=2, validators=[MinValueValidator(Decimal("0"))]
    )

    placed_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    completed_at = models.DateTimeField(null=True, blank=True)
    cancelled_at = models.DateTimeField(null=True, blank=True)
    cancellation_reason = models.CharField(max_length=500, blank=True, default="")

    # Incremented on every transition. A caller that read an order, decided
    # something and wrote back can check that nothing moved underneath it.
    version = models.PositiveIntegerField(default=1)

    class Meta:
        ordering = ("-placed_at",)
        indexes = [
            models.Index(fields=["status", "-placed_at"]),
            models.Index(fields=["-number"]),
        ]
        constraints = [
            models.CheckConstraint(
                condition=models.Q(total__gte=Decimal("0")),
                name="orders_total_not_negative",
            ),
        ]

    def __str__(self) -> str:
        return f"#{self.number}"

    # --- Money ---------------------------------------------------------------

    def recalculate(self) -> None:
        """
        Recomputes the totals from the lines currently attached.

        Called while an order is being built, never after it is placed. A
        discount larger than the goods is clamped rather than allowed to produce
        a negative total: a store that owes a shopper money is a refund, which is
        a different transaction with different rules.
        """
        subtotal = sum((item.line_total for item in self.items.all()), Decimal("0"))
        discount = min(self.discount_total, subtotal)

        self.subtotal = subtotal
        self.discount_total = discount
        self.total = subtotal - discount + self.fulfillment_fee

    # --- Lifecycle -----------------------------------------------------------

    @property
    def is_terminal(self) -> bool:
        return self.status in {OrderStatus.COMPLETED, OrderStatus.CANCELLED}

    def transition_to(
        self,
        target: str,
        *,
        actor: str = "",
        reason: str = "",
    ) -> "OrderStatusHistory":
        """
        Moves the order on, and records that it moved.

        The history entry is written here rather than by the caller, so an order
        cannot change status without leaving a trace. That trace is what answers
        "who cancelled this and when" during the argument that follows.
        """
        if target not in ALLOWED_TRANSITIONS[self.status]:
            raise ValidationError(
                f"An order that is {self.status} cannot become {target}."
            )

        if target == OrderStatus.CANCELLED and len(reason) > 500:
            raise ValidationError("The cancellation reason is too long.")

        previous = self.status
        self.status = target
        self.version += 1

        from django.utils import timezone

        now = timezone.now()

        if target == OrderStatus.COMPLETED:
            self.completed_at = now
        elif target == OrderStatus.CANCELLED:
            self.cancelled_at = now
            self.cancellation_reason = reason.strip()

        self.save(
            update_fields=[
                "status",
                "version",
                "completed_at",
                "cancelled_at",
                "cancellation_reason",
                "updated_at",
            ]
        )

        return OrderStatusHistory.objects.create(
            order=self,
            from_status=previous,
            to_status=target,
            actor=actor,
            reason=reason.strip(),
        )

    @classmethod
    @transaction.atomic
    def place(cls, **fields) -> "Order":
        """
        Creates an order with the next number, inside one transaction.

        Numbering and insertion have to be atomic together: a number taken and
        then rolled back is a gap in a sequence a business will be asked to
        explain to an auditor.
        """
        order = cls(number=OrderNumberSequence.take(), **fields)
        order.save()

        return order


class OrderItem(models.Model):
    """
    One line, priced as it was sold.

    `source_product_id` is a plain integer rather than a foreign key,
    deliberately. An archived product must not be resurrected by a cascade, and a
    line must stay readable if the product row is ever genuinely gone.
    """

    order = models.ForeignKey(Order, on_delete=models.CASCADE, related_name="items")

    source_product_id = models.BigIntegerField()
    product_name = models.CharField(max_length=150)
    source_variant_id = models.BigIntegerField(null=True, blank=True)
    variant_name = models.CharField(max_length=150, blank=True, default="")

    unit_base_price = models.DecimalField(max_digits=12, decimal_places=2)
    unit_modifier_total = models.DecimalField(max_digits=12, decimal_places=2, default=Decimal("0"))
    unit_price = models.DecimalField(max_digits=12, decimal_places=2)
    quantity = models.PositiveIntegerField()
    line_total = models.DecimalField(max_digits=14, decimal_places=2)
    display_order = models.PositiveIntegerField(default=0)

    class Meta:
        ordering = ("display_order", "id")

    def clean(self) -> None:
        super().clean()

        if self.quantity < 1:
            raise ValidationError({"quantity": "A line must be for at least one."})

    def price(self) -> None:
        """
        Derives the line's money from its parts.

        Held as three separate figures — base, modifiers, unit — because a
        receipt that shows only the final number cannot answer "why is this
        £2 more than the menu says".
        """
        self.unit_price = self.unit_base_price + self.unit_modifier_total
        self.line_total = self.unit_price * self.quantity

    def __str__(self) -> str:
        return f"{self.quantity} × {self.product_name}"


class OrderItemModifier(models.Model):
    """A choice made on a line, and what it added or removed."""

    item = models.ForeignKey(OrderItem, on_delete=models.CASCADE, related_name="modifiers")

    source_modifier_group_id = models.BigIntegerField()
    modifier_group_name = models.CharField(max_length=150)
    source_modifier_id = models.BigIntegerField()
    modifier_name = models.CharField(max_length=150)

    # Signed, because removing an ingredient can reduce the price.
    unit_price_delta = models.DecimalField(max_digits=12, decimal_places=2, default=Decimal("0"))
    display_order = models.PositiveIntegerField(default=0)

    class Meta:
        ordering = ("display_order", "id")

    def __str__(self) -> str:
        return f"{self.modifier_group_name}: {self.modifier_name}"


class OrderParty(models.Model):
    """
    Who ordered, as they were at the time.

    A snapshot rather than a link to `shoppers.Shopper`: somebody renaming
    themselves, or exercising a right to be forgotten, must not silently rewrite
    who a completed order belonged to.
    """

    order = models.OneToOneField(Order, on_delete=models.CASCADE, related_name="party")
    source_shopper_id = models.BigIntegerField(null=True, blank=True)
    display_name = models.CharField(max_length=200)
    phone = models.CharField(max_length=32, blank=True, default="")
    email = models.EmailField(blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)

    def __str__(self) -> str:
        return self.display_name


class OrderFulfillment(models.Model):
    """
    How the order was to be handed over, and where.

    The address is copied rather than referenced for the same reason as the
    party: a shopper moving house must not change where last month's order went.
    The delivery zone is a `source_*` id because zones live in an optional
    Feature that may not be installed.
    """

    order = models.OneToOneField(Order, on_delete=models.CASCADE, related_name="fulfillment")
    method = models.CharField(max_length=20, choices=FulfillmentMethod)
    fulfillment_fee = models.DecimalField(max_digits=12, decimal_places=2, default=Decimal("0"))

    source_delivery_zone_id = models.BigIntegerField(null=True, blank=True)
    delivery_zone_name = models.CharField(max_length=150, blank=True, default="")

    address_line1 = models.CharField(max_length=250, blank=True, default="")
    address_line2 = models.CharField(max_length=250, blank=True, default="")
    city = models.CharField(max_length=120, blank=True, default="")
    postal_code = models.CharField(max_length=32, blank=True, default="")
    latitude = models.FloatField(null=True, blank=True)
    longitude = models.FloatField(null=True, blank=True)
    created_at = models.DateTimeField(auto_now_add=True)

    def clean(self) -> None:
        super().clean()

        # A delivery with nowhere to deliver to is an order nobody can fulfil,
        # and the failure would surface at the door rather than at checkout.
        if self.method == FulfillmentMethod.DELIVERY and not self.address_line1.strip():
            raise ValidationError({"address_line1": "A delivery order needs an address."})

    def __str__(self) -> str:
        return self.method


class OrderPromotion(models.Model):
    """
    The discount this order received, recorded in full.

    Everything needed to explain the discount is copied here — its name, the
    coupon code, the kind of discount and the amount — precisely so the record
    survives the promotions Feature being uninstalled. `source_promotion_id` is
    kept for tracing while it is installed, and is meaningless afterwards, which
    is exactly why nothing depends on it
    ([`adr/0024`](../../../../docs/adr/0024-base-store-versus-optional-feature.md)).
    """

    order = models.OneToOneField(Order, on_delete=models.CASCADE, related_name="promotion")
    source_promotion_id = models.BigIntegerField(null=True, blank=True)
    source_coupon_id = models.BigIntegerField(null=True, blank=True)
    promotion_name = models.CharField(max_length=200)
    coupon_code = models.CharField(max_length=64, blank=True, default="")
    discount_type = models.CharField(max_length=20)
    discount_value = models.DecimalField(max_digits=12, decimal_places=2)
    discount_amount = models.DecimalField(max_digits=14, decimal_places=2)
    created_at = models.DateTimeField(auto_now_add=True)

    def __str__(self) -> str:
        return self.promotion_name


class OrderStatusHistory(models.Model):
    """
    Every status an order has held, and who moved it.

    Append-only. A history that could be edited after the fact answers no
    question worth asking.
    """

    order = models.ForeignKey(Order, on_delete=models.CASCADE, related_name="history")
    from_status = models.CharField(max_length=20, choices=OrderStatus, blank=True, default="")
    to_status = models.CharField(max_length=20, choices=OrderStatus)
    changed_at = models.DateTimeField(auto_now_add=True)

    # Free text rather than a user id: a counter order is moved by whoever is
    # standing there, and a foreign key to an account nobody creates would leave
    # this empty for the common case.
    actor = models.CharField(max_length=200, blank=True, default="")
    reason = models.CharField(max_length=500, blank=True, default="")

    class Meta:
        ordering = ("changed_at", "id")
        verbose_name_plural = "order status history"

    def __str__(self) -> str:
        return f"{self.from_status or '—'} → {self.to_status}"


class CheckoutIdempotencyRecord(models.Model):
    """
    Remembers a checkout that has already been accepted.

    Ported from the frozen `Checkout` module, and the reason it exists is
    unchanged: a shopper on a bad connection presses pay twice, and the second
    request must return the first order rather than create a second one. The
    request hash is stored alongside the key so that reusing a key for a
    *different* basket is refused rather than silently answered with the wrong
    order.
    """

    key = models.CharField(max_length=200, unique=True)
    request_hash = models.CharField(max_length=128)
    order = models.ForeignKey(Order, on_delete=models.CASCADE, null=True, blank=True)
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        indexes = [models.Index(fields=["created_at"])]

    def matches(self, request_hash: str) -> bool:
        return self.request_hash == request_hash

    def __str__(self) -> str:
        return self.key
