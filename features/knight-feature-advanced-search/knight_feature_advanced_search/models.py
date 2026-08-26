"""
The search index.

An **index**, not a view over the catalogue — and that distinction is the whole
design. A Feature may not import store business code, so this package cannot
read `apps.catalog` and must not try. The store pushes documents in; this ranks
them and hands back ids. Neither side sees the other's models, which is what
makes the Feature removable and what makes it work for a store whose catalogue
looks nothing like the reference one.

It is also how a search feature is actually built. Querying the live tables with
`LIKE` is what the base store already does; the reason to buy this is the
inverted index, and an index is a copy by definition.

PostgreSQL's own full-text search, deliberately. No Elasticsearch, no
OpenSearch, no external cluster: the point of this Feature in phase 13 is to put
a second category of package through the delivery engine, not to test
distributed infrastructure ([`../../../TODO.md`](../../../TODO.md), phase 13).
`SearchAdapter` keeps the door open for a provider later without the store's call
sites changing.
"""

from django.contrib.postgres.indexes import GinIndex
from django.contrib.postgres.search import SearchVectorField
from django.db import models


class DocumentType(models.TextChoices):
    """
    What kind of thing a document describes.

    Open-ended on purpose — a store with menus indexes `product` and one with
    articles might index `page`. The Feature does not care; it ranks text.
    """

    PRODUCT = "product", "Product"
    CATEGORY = "category", "Category"
    PAGE = "page", "Page"


class SearchDocument(models.Model):
    """
    One indexed thing, as text.

    `object_id` is a plain integer referring to a row in the store's own tables,
    and there is deliberately no foreign key to it. The consequence is stated
    rather than hidden: this index can go stale. A product renamed and not
    reindexed is found under its old name until it is, which is true of every
    search index ever built and is why `reindex` exists and why `indexed_at`
    is readable.
    """

    object_type = models.CharField(max_length=32, choices=DocumentType, default=DocumentType.PRODUCT)
    object_id = models.BigIntegerField()

    title = models.CharField(max_length=300)
    body = models.TextField(blank=True, default="")

    # Extra terms a shopper might search that are not in the title or body —
    # a SKU, a synonym, a misspelling the store knows people type. Weighted
    # above the body so "yirg" finds the coffee whose description never says it.
    keywords = models.CharField(max_length=500, blank=True, default="")

    # Kept as a column rather than computed per query. A tsvector built at query
    # time cannot use an index, which turns the one thing this Feature is for
    # into a full table scan.
    search_vector = SearchVectorField(null=True, blank=True)

    # For filtering without going back to the store. Nullable because not every
    # kind of document has either.
    category_id = models.BigIntegerField(null=True, blank=True, db_index=True)
    is_available = models.BooleanField(default=True)

    indexed_at = models.DateTimeField(auto_now=True)

    class Meta:
        db_table = "knight_search_document"
        ordering = ("title",)
        indexes = [
            # The index that makes this a search feature rather than a slow LIKE.
            GinIndex(fields=["search_vector"], name="knight_search_vector_gin"),
            models.Index(fields=["object_type", "is_available"], name="knight_search_type_avail"),
            # Trigrams over the title, for the typo pass. A separate index from
            # the tsvector one because it answers a different question: the
            # vector knows which words a document contains, and this knows which
            # documents contain something *like* what was typed. Added in 1.1.0
            # with the pg_trgm extension it needs
            # (docs/adr/0031-database-extensions-are-declared-not-migrated.md).
            GinIndex(fields=["title"], name="knight_search_title_trgm", opclasses=["gin_trgm_ops"]),
        ]
        constraints = [
            models.UniqueConstraint(
                fields=["object_type", "object_id"],
                name="knight_search_one_document_per_object",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.object_type} {self.object_id}: {self.title}"
