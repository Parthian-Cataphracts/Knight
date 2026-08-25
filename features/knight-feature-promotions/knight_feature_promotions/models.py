"""
Merchandising beyond a coupon code.

Plain coupons, percentage and fixed discounts, validity windows and minimums are
the base store's, as of the catalogue revision
([`adr/0024`](../../../docs/adr/0024-base-store-versus-optional-feature.md)).
What is sold is the sophistication: buy X get Y, bundles, and the decision about
whether a campaign may stack with the coupon a shopper also presented.

**This package does not extend the base store's promotion tables, and must not.**
A Feature may never import store business code, so these rules own their own
tables and answer through `services.price()`, which takes plain basket lines and
returns plain data. The base store decides whether to ask and what to do with the
answer. That is what keeps uninstalling this a matter of an app disappearing
rather than of the store having been built around it.

Products are referenced by plain id for the same reason: the catalogue lives in
the base store, and a foreign key into it would be a database-level coupling
between an optional package and the image.
"""

from decimal import Decimal

from django.core.exceptions import ValidationError
from django.core.validators import MinValueValidator
from django.db import models
from django.utils import timezone


class CampaignStatus(models.TextChoices):
    DRAFT = "Draft", "Draft"
    ACTIVE = "Active", "Active"
    ARCHIVED = "Archived", "Archived"


class Campaign(models.Model):
    """
    What every advanced rule has in common: a name, a window, and how it behaves
    beside other discounts.

    Abstract rather than a shared table. The two rule types have nothing in
    common in their arithmetic, and a single table with half its columns null
    per row is how a pricing engine becomes impossible to reason about.
    """

    name = models.CharField(max_length=200)
    description = models.TextField(max_length=1000, blank=True, default="")
    status = models.CharField(max_length=20, choices=CampaignStatus, default=CampaignStatus.DRAFT)

    starts_at = models.DateTimeField(null=True, blank=True)
    ends_at = models.DateTimeField(null=True, blank=True)

    # Which rule wins when several qualify. Highest first, ties by id.
    priority = models.IntegerField(default=0)

    # Whether this may be added to a discount the base store already found.
    # False by default, and that default is the safe one: two rules applying in
    # full is how a basket ends up discounted twice for the same reason. A
    # merchant who means "as well as their coupon" has to say so.
    stacks = models.BooleanField(default=False)

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    archived_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        abstract = True
        ordering = ("-priority", "id")

    def clean(self) -> None:
        super().clean()

        if self.ends_at and self.starts_at and self.ends_at <= self.starts_at:
            raise ValidationError({"ends_at": "A campaign cannot end before it starts."})

    def is_live(self, at=None) -> bool:
        moment = at or timezone.now()

        if self.status != CampaignStatus.ACTIVE:
            return False

        if self.starts_at and moment < self.starts_at:
            return False

        if self.ends_at and moment >= self.ends_at:
            return False

        return True

    def archive(self) -> None:
        self.status = CampaignStatus.ARCHIVED
        self.archived_at = timezone.now()

    def __str__(self) -> str:
        return self.name


class BuyXGetY(Campaign):
    """
    Buy a quantity of one product, get another cheaper or free.

    The reward is expressed as a percentage off the reward product rather than a
    fixed amount, because "get one free" and "get the second half price" are the
    same rule at 100 and 50 — and a merchant who has to choose between two
    different features to express them will pick the wrong one.
    """

    trigger_product_id = models.BigIntegerField()
    trigger_quantity = models.PositiveIntegerField(default=1)

    reward_product_id = models.BigIntegerField()
    reward_quantity = models.PositiveIntegerField(default=1)
    reward_percent = models.DecimalField(
        max_digits=5, decimal_places=2, default=Decimal("100"),
        validators=[MinValueValidator(Decimal("0"))],
    )

    # How many times one basket may earn this. Null is unlimited; a shopper
    # buying twelve of the trigger gets six rewards, which is usually what a
    # "buy one get one" means and occasionally ruinous, hence the cap.
    maximum_awards_per_order = models.PositiveIntegerField(null=True, blank=True)

    class Meta(Campaign.Meta):
        abstract = False
        verbose_name_plural = "buy X get Y campaigns"

    def clean(self) -> None:
        super().clean()

        if self.reward_percent > Decimal("100"):
            raise ValidationError({"reward_percent": "A reward cannot exceed 100 percent off."})

        if self.trigger_quantity < 1:
            raise ValidationError({"trigger_quantity": "A trigger needs at least one item."})

    def discount_for(self, lines) -> Decimal:
        """
        What this campaign takes off a basket.

        Returns zero when the basket does not qualify. The reward is only ever
        given on reward items actually present: a campaign cannot add goods to a
        basket, only make what is there cheaper, and pricing a reward the shopper
        is not buying would discount an item that never ships.
        """
        by_product: dict[int, object] = {line.product_id: line for line in lines}

        trigger = by_product.get(self.trigger_product_id)
        reward = by_product.get(self.reward_product_id)

        if trigger is None or reward is None:
            return Decimal("0")

        awards = trigger.quantity // self.trigger_quantity

        if awards <= 0:
            return Decimal("0")

        if self.maximum_awards_per_order is not None:
            awards = min(awards, self.maximum_awards_per_order)

        # When the trigger and the reward are the same product, the items that
        # earned the reward cannot also be the reward. Otherwise "buy 2 get 1
        # free" on one product discounts all three.
        available = reward.quantity

        if self.reward_product_id == self.trigger_product_id:
            available = max(0, reward.quantity - awards * self.trigger_quantity)

        rewarded = min(awards * self.reward_quantity, available)

        if rewarded <= 0:
            return Decimal("0")

        return (reward.unit_price * rewarded * self.reward_percent / Decimal("100")).quantize(
            Decimal("0.01")
        )


class Bundle(Campaign):
    """
    A set of products for one price.

    The discount is the difference between what the items cost separately and
    the bundle price, which means the saving follows the catalogue: a merchant
    who raises a price does not have to remember to restate the bundle's saving.
    """

    bundle_price = models.DecimalField(
        max_digits=12, decimal_places=2, validators=[MinValueValidator(Decimal("0"))]
    )

    class Meta(Campaign.Meta):
        abstract = False

    def discount_for(self, lines) -> Decimal:
        """
        What this bundle takes off a basket.

        Awarded whole: a basket either contains every item in the required
        quantity or the bundle does not apply. A partial bundle at a partial
        discount is a different product and a merchant has not asked for it.
        """
        items = list(self.items.all())

        if not items:
            return Decimal("0")

        by_product = {line.product_id: line for line in lines}
        list_total = Decimal("0")

        for item in items:
            line = by_product.get(item.product_id)

            if line is None or line.quantity < item.quantity:
                return Decimal("0")

            list_total += line.unit_price * item.quantity

        saving = list_total - self.bundle_price

        return saving if saving > Decimal("0") else Decimal("0")


class BundleItem(models.Model):
    """One product, and how many of it the bundle requires."""

    bundle = models.ForeignKey(Bundle, on_delete=models.CASCADE, related_name="items")
    product_id = models.BigIntegerField()
    quantity = models.PositiveIntegerField(default=1)

    class Meta:
        ordering = ("id",)
        constraints = [
            models.UniqueConstraint(
                fields=["bundle", "product_id"],
                name="advanced_promotions_bundle_product_once",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.quantity} x product {self.product_id}"
