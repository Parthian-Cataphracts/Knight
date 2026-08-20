"""
The store's catalogue: what it sells, and how each thing can be configured.

Ported from the frozen .NET `Catalog` module. Two deliberate differences, both
from [`adr/0023`](../../../../docs/adr/0023-a-ported-store-is-single-tenant.md):

* **No `tenant_id`.** This store is the tenant. Isolation is a separate database
  with separate credentials, not a column somebody has to remember to filter on.
  Every uniqueness constraint that was `(tenant, slug)` is now just `slug`.
* **Removal is archival.** A product is archived rather than deleted, because
  orders placed months ago still name it and a history that cannot resolve its
  own line items is not a history.

Prices are `Decimal`, never float. A tenth of a cent lost to binary rounding is
a rounding error in a spreadsheet and a reconciliation failure in a business.
"""

from decimal import Decimal

from django.core.exceptions import ValidationError
from django.core.validators import MinValueValidator
from django.db import models

from .slugs import normalize


class ProductStatus(models.TextChoices):
    """
    Stored as its name rather than an integer, so a database dump stays readable
    and inserting a new member cannot silently change what existing rows mean.
    """

    DRAFT = "Draft", "Draft"
    ACTIVE = "Active", "Active"
    ARCHIVED = "Archived", "Archived"


class SluggedModel(models.Model):
    """Shared behaviour for the two things a shopper can reach by name."""

    name = models.CharField(max_length=150)
    slug = models.SlugField(max_length=150, unique=True)
    description = models.TextField(max_length=2000, blank=True, default="")
    is_visible = models.BooleanField(default=True)
    display_order = models.PositiveIntegerField(default=0)
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        abstract = True

    def clean(self) -> None:
        # Normalised on the way in, so the unique index is enforcing the same
        # notion of sameness the application uses. Doing it at read time would
        # let two rows exist that the application considers identical.
        super().clean()
        self.slug = normalize(self.slug or self.name)

    def save(self, *args, **kwargs):
        self.slug = normalize(self.slug or self.name)

        return super().save(*args, **kwargs)

    def __str__(self) -> str:
        return self.name


class Category(SluggedModel):
    """A grouping of products. Flat by design: nested categories are a merchandising feature, not a catalogue one."""

    class Meta:
        ordering = ("display_order", "name")
        verbose_name_plural = "categories"


class Product(SluggedModel):
    """
    Something the store sells, belonging to exactly one category.

    `is_available` and `is_visible` are separate on purpose, and the distinction
    is the one shopkeepers actually make: *visible* is whether it appears in the
    catalogue at all, *available* is whether it can be bought right now. A
    sold-out item that vanishes from the menu is a support call; one that shows
    as unavailable is an explanation.
    """

    category = models.ForeignKey(Category, on_delete=models.PROTECT, related_name="products")
    status = models.CharField(max_length=20, choices=ProductStatus, default=ProductStatus.DRAFT)
    base_price = models.DecimalField(
        max_digits=12,
        decimal_places=2,
        validators=[MinValueValidator(Decimal("0"))],
    )
    is_available = models.BooleanField(default=True)

    class Meta:
        ordering = ("display_order", "name")
        indexes = [
            # The shopper-facing query: what can I buy in this category, in order.
            models.Index(fields=["category", "status", "display_order"]),
        ]

    @property
    def is_orderable(self) -> bool:
        """
        Whether this can go into a basket right now.

        Read by ordering rather than re-derived there, so the two can never
        disagree about what "sellable" means.
        """
        return self.status == ProductStatus.ACTIVE and self.is_available and self.is_visible

    def archive(self) -> None:
        """
        Withdraws the product without deleting it.

        Orders keep naming it, so the row has to survive. Archiving also hides
        it, because a withdrawn product that stayed on the menu would be a
        shopper's disappointment rather than a merchant's decision.
        """
        self.status = ProductStatus.ARCHIVED
        self.is_visible = False
        self.is_available = False


class ProductVariant(models.Model):
    """
    One buyable form of a product — a size, a colour, a weight.

    A product always has at least one, and exactly one of them is the default.
    That invariant is what lets ordering treat "a product" and "a variant of a
    product" as the same thing at the point of sale.
    """

    product = models.ForeignKey(Product, on_delete=models.CASCADE, related_name="variants")
    name = models.CharField(max_length=150)
    sku = models.CharField(max_length=100, blank=True, default="")

    # The SKU as matched, not as typed. Merchants type SKUs inconsistently and
    # then expect " ABC-1 " and "abc-1" to be the same thing.
    normalized_sku = models.CharField(max_length=100, blank=True, default="", editable=False)

    price = models.DecimalField(
        max_digits=12,
        decimal_places=2,
        validators=[MinValueValidator(Decimal("0"))],
    )

    # What it used to cost, for showing a saving. Never used in pricing: a
    # display figure that fed the total would be a discount nobody authorised.
    compare_at_price = models.DecimalField(
        max_digits=12,
        decimal_places=2,
        null=True,
        blank=True,
        validators=[MinValueValidator(Decimal("0"))],
    )

    is_default = models.BooleanField(default=False)
    is_available = models.BooleanField(default=True)
    display_order = models.PositiveIntegerField(default=0)
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        ordering = ("display_order", "name")
        constraints = [
            # A store-wide SKU, where one is given. Enforced in the database
            # rather than in a save hook, because two concurrent imports are
            # exactly how duplicate SKUs get created.
            models.UniqueConstraint(
                fields=["normalized_sku"],
                condition=~models.Q(normalized_sku=""),
                name="catalog_variant_unique_sku",
            ),
            # At most one default per product. Partial, so the many non-default
            # variants do not collide with each other.
            models.UniqueConstraint(
                fields=["product"],
                condition=models.Q(is_default=True),
                name="catalog_variant_single_default",
            ),
        ]

    def clean(self) -> None:
        super().clean()

        if self.compare_at_price is not None and self.compare_at_price < self.price:
            # A "was" price below the current one is not a saving, and showing it
            # as one would be a claim the merchant did not mean to make.
            raise ValidationError(
                {"compare_at_price": "The compare-at price cannot be below the price."}
            )

    def save(self, *args, **kwargs):
        self.normalized_sku = (self.sku or "").strip().upper()

        return super().save(*args, **kwargs)

    def __str__(self) -> str:
        return f"{self.product.name} — {self.name}"


class ModifierGroup(models.Model):
    """
    A choice a shopper makes about a product: size, extras, how it is cooked.

    The selection bounds live here rather than on the product, because the same
    group is attached to many products and the rule travels with the question,
    not with the thing being asked about.
    """

    name = models.CharField(max_length=150)
    is_required = models.BooleanField(default=False)
    min_selections = models.PositiveIntegerField(default=0)
    max_selections = models.PositiveIntegerField(default=1)
    display_order = models.PositiveIntegerField(default=0)
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    products = models.ManyToManyField(
        Product,
        through="ProductModifierGroup",
        related_name="modifier_groups",
    )

    class Meta:
        ordering = ("display_order", "name")
        constraints = [
            models.CheckConstraint(
                condition=models.Q(max_selections__gte=models.F("min_selections")),
                name="catalog_group_max_at_least_min",
            ),
        ]

    def clean(self) -> None:
        super().clean()

        if self.max_selections < self.min_selections:
            raise ValidationError(
                {"max_selections": "The maximum cannot be below the minimum."}
            )

        if self.is_required and self.min_selections == 0:
            # A required group that permits zero selections is not required, and
            # the contradiction would surface as an order that validated and
            # should not have.
            raise ValidationError(
                {"min_selections": "A required group must ask for at least one selection."}
            )

    def __str__(self) -> str:
        return self.name


class Modifier(models.Model):
    """One option within a group, and what choosing it does to the price."""

    group = models.ForeignKey(ModifierGroup, on_delete=models.CASCADE, related_name="modifiers")
    name = models.CharField(max_length=150)

    # Signed: a modifier may make something cheaper. Removing an ingredient is a
    # discount, and a field that could not express one would force merchants to
    # model it as a separate product.
    price_delta = models.DecimalField(max_digits=12, decimal_places=2, default=Decimal("0"))

    is_available = models.BooleanField(default=True)
    display_order = models.PositiveIntegerField(default=0)
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        ordering = ("display_order", "name")

    def __str__(self) -> str:
        return self.name


class ProductModifierGroup(models.Model):
    """
    Attaches a group to a product, in a position chosen per product.

    The order is here rather than on the group because the same question can
    reasonably be asked first on one product and last on another.
    """

    product = models.ForeignKey(Product, on_delete=models.CASCADE)
    group = models.ForeignKey(ModifierGroup, on_delete=models.CASCADE)
    display_order = models.PositiveIntegerField(default=0)
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        ordering = ("display_order",)
        constraints = [
            models.UniqueConstraint(
                fields=["product", "group"],
                name="catalog_product_group_unique",
            ),
        ]


class ProductMedia(models.Model):
    """
    An image of a product, held as a storage key rather than a URL.

    A URL would bake the storage host into the database and turn moving buckets
    into a data migration. The key is what the store owns; the address is what
    whoever serves it decides.
    """

    product = models.ForeignKey(Product, on_delete=models.CASCADE, related_name="media")
    storage_key = models.CharField(max_length=500)
    alt_text = models.CharField(max_length=300, blank=True, default="")
    display_order = models.PositiveIntegerField(default=0)
    is_primary = models.BooleanField(default=False)
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        ordering = ("display_order", "created_at")
        constraints = [
            models.UniqueConstraint(
                fields=["product"],
                condition=models.Q(is_primary=True),
                name="catalog_media_single_primary",
            ),
        ]

    def __str__(self) -> str:
        return self.storage_key
