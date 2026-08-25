"""
What shoppers said about what they bought.

Owned entirely by this feature. Nothing in the store imports these models and
this feature imports nothing from the store, so uninstalling it cannot break a
product page — the page simply stops showing reviews
([`adr/0024`](../../../docs/adr/0024-base-store-versus-optional-feature.md)).

Products and shoppers are referenced by **plain id**, never by foreign key. The
catalogue and the shopper table live in the base store, and a database-level
relationship from an optional package into the image is the one coupling that
would make this Feature impossible to remove. The cost is real and accepted: no
cascade, so a product deleted in the base store leaves reviews behind. That is
why the base store archives rather than deletes
([`adr/0023`](../../../docs/adr/0023-a-ported-store-is-single-tenant.md)), and
why `published_for()` is asked for one product at a time by a caller that knows
the product exists.
"""

from django.core.exceptions import ValidationError
from django.core.validators import MaxValueValidator, MinValueValidator
from django.db import models
from django.utils import timezone


class ReviewStatus(models.TextChoices):
    """
    Reviews arrive unpublished.

    Moderation-by-default rather than publish-by-default: the first thing an
    open review box attracts is spam, and a store that has to remove it after
    the fact has already shown it to shoppers.
    """

    PENDING = "Pending", "Awaiting moderation"
    PUBLISHED = "Published", "Published"
    REJECTED = "Rejected", "Rejected"


class Review(models.Model):
    """
    One shopper's verdict on one product.

    `is_verified_purchase` is asserted by the caller rather than derived here.
    This feature cannot see the order table — it may not import store business
    code — so the store, which can, says whether the reviewer actually bought
    the thing. A Feature that guessed would be a Feature that lied on a badge
    shoppers are asked to trust.
    """

    product_id = models.BigIntegerField(db_index=True)

    # Null for a review left by somebody who was not signed in. Kept nullable
    # rather than required because a store may well want reviews from guests,
    # and the alternative is inventing a shopper row for every one of them.
    shopper_id = models.BigIntegerField(null=True, blank=True, db_index=True)

    author_name = models.CharField(max_length=120)
    rating = models.PositiveSmallIntegerField(
        validators=[MinValueValidator(1), MaxValueValidator(5)]
    )
    title = models.CharField(max_length=200, blank=True, default="")
    body = models.TextField(max_length=4000, blank=True, default="")

    status = models.CharField(max_length=16, choices=ReviewStatus, default=ReviewStatus.PENDING)
    is_verified_purchase = models.BooleanField(default=False)

    # The order the purchase was verified against, for tracing while this
    # feature is installed. Meaningless afterwards, which is exactly why nothing
    # depends on it.
    source_order_id = models.BigIntegerField(null=True, blank=True)

    # Why a review was rejected. For the merchant's own record, and never shown
    # to a shopper: a rejection reason is a note to staff, not a reply.
    moderator_note = models.CharField(max_length=500, blank=True, default="")

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    published_at = models.DateTimeField(null=True, blank=True)
    moderated_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        db_table = "knight_review"
        ordering = ("-created_at", "-id")
        indexes = [
            # The query a product page runs, and the only one on the hot path.
            models.Index(fields=["product_id", "status"], name="knight_review_prod_status"),
        ]
        constraints = [
            models.UniqueConstraint(
                fields=["product_id", "shopper_id"],
                condition=models.Q(shopper_id__isnull=False),
                name="knight_review_once_per_shopper_and_product",
            ),
            models.CheckConstraint(
                condition=models.Q(rating__gte=1) & models.Q(rating__lte=5),
                name="knight_review_rating_between_one_and_five",
            ),
        ]

    def clean(self) -> None:
        super().clean()

        if not self.author_name.strip():
            raise ValidationError({"author_name": "A review needs a name to attribute it to."})

    def publish(self) -> None:
        moment = timezone.now()
        self.status = ReviewStatus.PUBLISHED
        self.published_at = moment
        self.moderated_at = moment

    def reject(self, note: str = "") -> None:
        """
        Rejects without deleting.

        A store accused of hiding criticism needs to be able to show what it
        rejected and why, and a deleted row answers nothing.
        """
        self.status = ReviewStatus.REJECTED
        self.moderator_note = note[:500]
        self.moderated_at = timezone.now()
        self.published_at = None

    def __str__(self) -> str:
        return f"{self.rating}/5 on product {self.product_id} by {self.author_name}"


class ReviewReply(models.Model):
    """
    The merchant's answer, shown beneath the review.

    One per review. A thread would be a conversation, and a conversation needs
    notification, moderation and abuse handling that a reviews feature has no
    business growing into.
    """

    review = models.OneToOneField(Review, on_delete=models.CASCADE, related_name="reply")
    body = models.TextField(max_length=2000)
    author_name = models.CharField(max_length=120, default="The store")
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        db_table = "knight_review_reply"

    def __str__(self) -> str:
        return f"Reply to review {self.review_id}"
