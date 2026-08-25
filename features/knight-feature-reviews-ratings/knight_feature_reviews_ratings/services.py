"""
The surface the store and other features may call.

Plain data in, plain data out. A caller hands over a product id and gets back
counts and published text; nothing here returns a model, so a store page that
renders reviews does not end up holding this feature's ORM objects and does not
have to change when their storage does.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from decimal import Decimal

from django.db.models import Avg, Count

from .models import Review, ReviewReply, ReviewStatus


@dataclass(frozen=True)
class RatingSummary:
    """
    What a product page shows above the reviews.

    `average` is None rather than 0 when there are no reviews. Zero is a rating
    a product could genuinely have, and drawing an unreviewed product as one
    star is worse than drawing nothing.
    """

    product_id: int
    count: int
    average: Decimal | None
    distribution: dict[int, int] = field(default_factory=dict)

    @property
    def has_reviews(self) -> bool:
        return self.count > 0


@dataclass(frozen=True)
class PublishedReview:
    """One published review, flattened for rendering."""

    id: int
    author_name: str
    rating: int
    title: str
    body: str
    is_verified_purchase: bool
    published_at: object
    reply: str = ""


def submit(
    product_id: int,
    *,
    rating: int,
    author_name: str,
    title: str = "",
    body: str = "",
    shopper_id: int | None = None,
    is_verified_purchase: bool = False,
    source_order_id: int | None = None,
) -> Review:
    """
    Records a review, unpublished.

    Validated rather than trusted: `full_clean` runs the rating bounds and the
    name check, so a bad submission is refused here instead of reaching a
    product page. The caller decides what to tell the shopper.
    """
    review = Review(
        product_id=product_id,
        shopper_id=shopper_id,
        author_name=author_name.strip(),
        rating=rating,
        title=title.strip(),
        body=body.strip(),
        is_verified_purchase=is_verified_purchase,
        source_order_id=source_order_id,
    )

    review.full_clean()
    review.save()

    return review


def publish(review_id: int) -> bool:
    """Publishes one review. Answers whether it existed."""
    review = Review.objects.filter(pk=review_id).first()

    if review is None:
        return False

    review.publish()
    review.save(update_fields=["status", "published_at", "moderated_at", "updated_at"])

    return True


def reject(review_id: int, note: str = "") -> bool:
    """Rejects one review, keeping it. Answers whether it existed."""
    review = Review.objects.filter(pk=review_id).first()

    if review is None:
        return False

    review.reject(note)
    review.save(
        update_fields=["status", "moderator_note", "moderated_at", "published_at", "updated_at"]
    )

    return True


def reply(review_id: int, body: str, author_name: str = "The store") -> ReviewReply | None:
    """
    Adds or replaces the merchant's reply.

    Replacing rather than appending: a merchant correcting their own wording is
    the common case, and a second reply beneath the first reads as an argument.
    """
    review = Review.objects.filter(pk=review_id).first()

    if review is None:
        return None

    existing, _ = ReviewReply.objects.update_or_create(
        review=review, defaults={"body": body.strip(), "author_name": author_name.strip()}
    )

    return existing


def summary_for(product_id: int) -> RatingSummary:
    """
    The rating summary for one product, counting published reviews only.

    Computed rather than stored. A counter column would need every write path to
    maintain it and would drift the first time one did not; at the volume a
    single store's product page sees, the aggregate over an indexed column is
    the cheaper correctness.
    """
    published = Review.objects.filter(product_id=product_id, status=ReviewStatus.PUBLISHED)

    aggregate = published.aggregate(count=Count("id"), average=Avg("rating"))
    count = aggregate["count"] or 0

    if count == 0:
        return RatingSummary(product_id=product_id, count=0, average=None)

    distribution = {
        row["rating"]: row["n"]
        for row in published.values("rating").annotate(n=Count("id")).order_by("rating")
    }

    return RatingSummary(
        product_id=product_id,
        count=count,
        # Quantised on the way out: a product page showing 4.333333333 is a page
        # that has leaked a float into a design decision.
        average=Decimal(str(aggregate["average"])).quantize(Decimal("0.1")),
        distribution={star: distribution.get(star, 0) for star in range(1, 6)},
    )


def published_for(product_id: int, *, limit: int = 20, offset: int = 0) -> list[PublishedReview]:
    """
    Published reviews for one product, newest first.

    Bounded by default. An unbounded read is one query returning every review a
    popular product ever collected, on the page a shopper is waiting for.
    """
    limit = max(1, min(limit, 100))

    rows = (
        Review.objects.filter(product_id=product_id, status=ReviewStatus.PUBLISHED)
        .select_related("reply")
        .order_by("-published_at", "-id")[offset : offset + limit]
    )

    return [
        PublishedReview(
            id=review.pk,
            author_name=review.author_name,
            rating=review.rating,
            title=review.title,
            body=review.body,
            is_verified_purchase=review.is_verified_purchase,
            published_at=review.published_at,
            reply=getattr(review, "reply", None).body if hasattr(review, "reply") else "",
        )
        for review in rows
    ]


def pending(limit: int = 50) -> list[Review]:
    """
    The moderation queue, oldest first.

    Oldest first on purpose: a queue worked newest-first leaves the earliest
    reviews unseen for longest, which is the opposite of what a shopper waiting
    for theirs to appear experiences.
    """
    return list(Review.objects.filter(status=ReviewStatus.PENDING).order_by("created_at")[:limit])


def summaries_for(product_ids) -> dict[int, RatingSummary]:
    """
    Summaries for several products in one query, for a listing page.

    The N+1 this exists to prevent is the whole reason a category page would
    otherwise avoid showing stars at all.
    """
    ids = list(dict.fromkeys(product_ids))

    if not ids:
        return {}

    rows = (
        Review.objects.filter(product_id__in=ids, status=ReviewStatus.PUBLISHED)
        .values("product_id")
        .annotate(count=Count("id"), average=Avg("rating"))
    )

    found = {
        row["product_id"]: RatingSummary(
            product_id=row["product_id"],
            count=row["count"],
            average=Decimal(str(row["average"])).quantize(Decimal("0.1")),
        )
        for row in rows
    }

    # Every id asked for gets an entry, so a caller never has to distinguish
    # "no reviews" from "I forgot to handle a missing key".
    return {
        product_id: found.get(product_id, RatingSummary(product_id, 0, None))
        for product_id in ids
    }
