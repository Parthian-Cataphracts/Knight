"""
Indexing and searching.

Plain data in both directions. The store hands over documents built from its own
catalogue and gets back ranked ids and titles; it then loads whatever it wants to
render from its own tables. Nothing here returns a model, and nothing here reads
one of the store's.
"""

from __future__ import annotations

from dataclasses import dataclass
from decimal import Decimal

from django.contrib.postgres.search import (
    SearchQuery,
    SearchRank,
    SearchVector,
    TrigramSimilarity,
)
from django.db import transaction
from django.db.models import Count, F, QuerySet

from .models import DocumentType, SearchDocument

#: Field weights, highest first. A match in the title is worth more than one in
#: the body, and a keyword the merchant added on purpose sits between them —
#: it is a deliberate signal, unlike prose that happens to contain the word.
TITLE_WEIGHT = "A"
KEYWORD_WEIGHT = "B"
BODY_WEIGHT = "C"

#: Postgres text search configuration. English stemming is a real choice and the
#: wrong one for some catalogues; 'simple' does no stemming at all and is the
#: safer default for a store whose products are named rather than described.
CONFIG = "english"

#: How alike a title has to be to a misspelling before it counts as a match.
#:
#: 0.3 is PostgreSQL's own default for the `%` operator, and it is stated here as
#: well as relied on there: `pg_trgm.similarity_threshold` is a session setting,
#: so a store that changed it would silently change what this Feature returns.
#: Applied as an explicit floor, the two can only agree or this can be stricter.
#:
#: Lower would be worse than useless. A search box that answers "kettle" with
#: "cattle" has not been helpful, it has been confidently wrong, and a shopper
#: reads the results as what the shop sells rather than as guesses.
SIMILARITY_FLOOR = 0.3

#: Below this, a typo pass is guessing. Two- and three-character strings share
#: trigrams with half a catalogue.
MINIMUM_FUZZY_LENGTH = 4


@dataclass(frozen=True)
class Hit:
    """One result, in terms the store can resolve against its own tables."""

    object_type: str
    object_id: int
    title: str
    rank: float

    @property
    def score(self) -> Decimal:
        """The rank, rounded for display. Not for comparison — use `rank`."""
        return Decimal(str(self.rank)).quantize(Decimal("0.0001"))


@dataclass(frozen=True)
class Facet:
    """One filter value and how many documents carry it."""

    value: object
    count: int


def index(
    object_id: int,
    *,
    title: str,
    object_type: str = DocumentType.PRODUCT,
    body: str = "",
    keywords: str = "",
    category_id: int | None = None,
    is_available: bool = True,
) -> SearchDocument:
    """
    Adds or replaces one document.

    Upserted rather than appended: a product indexed twice is one document, and
    an index that accumulated a row per save would return the same product five
    times and get slower every time somebody edited it.
    """
    document, _ = SearchDocument.objects.update_or_create(
        object_type=object_type,
        object_id=object_id,
        defaults={
            "title": title,
            "body": body,
            "keywords": keywords,
            "category_id": category_id,
            "is_available": is_available,
        },
    )

    _refresh_vectors(SearchDocument.objects.filter(pk=document.pk))

    return document


@transaction.atomic
def index_many(documents) -> int:
    """
    Indexes a batch, then rebuilds the vectors once.

    One vector update for the whole batch rather than one per document: the
    update is a table-wide expression, and doing it per row is what makes a
    reindex of a real catalogue take minutes instead of seconds.
    """
    touched = []

    for document in documents:
        record, _ = SearchDocument.objects.update_or_create(
            object_type=document.get("object_type", DocumentType.PRODUCT),
            object_id=document["object_id"],
            defaults={
                "title": document.get("title", ""),
                "body": document.get("body", ""),
                "keywords": document.get("keywords", ""),
                "category_id": document.get("category_id"),
                "is_available": document.get("is_available", True),
            },
        )
        touched.append(record.pk)

    if touched:
        _refresh_vectors(SearchDocument.objects.filter(pk__in=touched))

    return len(touched)


def remove(object_id: int, *, object_type: str = DocumentType.PRODUCT) -> bool:
    """Drops one document. Answers whether there was one."""
    deleted, _ = SearchDocument.objects.filter(
        object_type=object_type, object_id=object_id
    ).delete()

    return deleted > 0


def clear(object_type: str | None = None) -> int:
    """
    Empties the index, or one type of it.

    For a full reindex, which is the honest way to recover from a store whose
    catalogue changed underneath a stale index.
    """
    queryset = SearchDocument.objects.all()

    if object_type is not None:
        queryset = queryset.filter(object_type=object_type)

    deleted, _ = queryset.delete()

    return deleted


def search(
    query: str,
    *,
    object_type: str | None = None,
    category_id: int | None = None,
    include_unavailable: bool = False,
    limit: int = 20,
    offset: int = 0,
) -> list[Hit]:
    """
    Ranked results for a query.

    Three passes, each a fallback for the one before, and each running only when
    the previous found nothing — so a query that matched properly is never
    diluted by weaker matches.

    1. **Full text.** Whole words, stemmed, ranked by where they appear.
    2. **Prefix.** A full-text match needs whole words, so a shopper who has
       typed "yirg" while still typing has matched nothing, and a search box that
       goes blank mid-word reads as broken.
    3. **Trigram similarity**, added in 1.1.0. The first two both need the letters
       to be right; this one is what answers "expresso" with the espresso
       machine. It is last because it is the only pass that can be *wrong* rather
       than merely empty.
    """
    text = (query or "").strip()

    if not text:
        return []

    limit = max(1, min(limit, 100))
    base = _filtered(object_type, category_id, include_unavailable)

    hits = _ranked(base, SearchQuery(text, config=CONFIG, search_type="websearch"), limit, offset)

    if hits:
        return hits

    hits = _ranked(base, _prefix_query(text), limit, offset)

    if hits:
        return hits

    return _similar(base, text, limit, offset)


def facets(
    query: str = "",
    *,
    object_type: str | None = None,
    include_unavailable: bool = False,
) -> dict[str, list[Facet]]:
    """
    Counts per filter value for the current result set.

    Counted over the matches rather than the whole index, because a facet that
    ignores the query offers filters that lead to nothing.
    """
    queryset = _filtered(object_type, None, include_unavailable)
    text = (query or "").strip()

    if text:
        queryset = queryset.filter(search_vector=SearchQuery(text, config=CONFIG, search_type="websearch"))

    by_type = (
        queryset.values("object_type")
        .annotate(count=Count("id"))
        .order_by("-count")
    )
    by_category = (
        queryset.exclude(category_id=None)
        .values("category_id")
        .annotate(count=Count("id"))
        .order_by("-count")
    )

    return {
        "objectType": [Facet(row["object_type"], row["count"]) for row in by_type],
        "categoryId": [Facet(row["category_id"], row["count"]) for row in by_category],
    }


def suggest(prefix: str, *, limit: int = 8) -> list[str]:
    """
    Titles that begin with what has been typed, for a search box.

    Titles rather than hits: a suggestion list is a list of things to search for,
    and returning ranks with it invites a caller to treat it as results.

    Falls back to similar titles when nothing starts with the text, which is the
    case a suggestion list is worst at: one mistyped letter and the dropdown
    empties, telling a shopper the shop has nothing rather than that they have a
    typo.
    """
    text = (prefix or "").strip()

    if len(text) < 2:
        return []

    limit = max(1, min(limit, 25))

    starting = list(
        SearchDocument.objects.filter(is_available=True, title__istartswith=text)
        .order_by("title")
        .values_list("title", flat=True)[:limit]
    )

    if starting or len(text) < MINIMUM_FUZZY_LENGTH:
        return starting

    return [hit.title for hit in _similar(_filtered(None, None, False), text, limit, 0)]


def stats() -> dict[str, int]:
    """How much is indexed, for an operator wondering whether a reindex ran."""
    counts = {
        row["object_type"]: row["count"]
        for row in SearchDocument.objects.values("object_type").annotate(count=Count("id"))
    }
    counts["total"] = SearchDocument.objects.count()

    return counts


# --- Internals --------------------------------------------------------------


def _filtered(object_type, category_id, include_unavailable) -> QuerySet:
    queryset = SearchDocument.objects.all()

    if object_type is not None:
        queryset = queryset.filter(object_type=object_type)

    if category_id is not None:
        queryset = queryset.filter(category_id=category_id)

    if not include_unavailable:
        # A shop must not offer what it cannot sell. Default rather than
        # optional, because the caller who forgets is the storefront.
        queryset = queryset.filter(is_available=True)

    return queryset


def _ranked(queryset: QuerySet, search_query, limit: int, offset: int) -> list[Hit]:
    rows = (
        queryset.filter(search_vector=search_query)
        .annotate(rank=SearchRank(F("search_vector"), search_query))
        .order_by("-rank", "title")[offset : offset + limit]
        .values("object_type", "object_id", "title", "rank")
    )

    return [
        Hit(
            object_type=row["object_type"],
            object_id=row["object_id"],
            title=row["title"],
            rank=float(row["rank"] or 0),
        )
        for row in rows
    ]


def _similar(queryset: QuerySet, text: str, limit: int, offset: int) -> list[Hit]:
    """
    Titles that are *like* what was typed, for when nothing matches what was
    actually typed.

    Filtered with `__trigram_similar` and then ranked by `TrigramSimilarity`,
    which looks redundant and is not. The filter compiles to the `%` operator,
    which the GIN trigram index can answer; a bare `similarity(...) >= 0.3` is a
    sequential scan over every document in the catalogue, computing a similarity
    for each. Same rows, and the difference on a real catalogue is the difference
    between a search box and a timeout.

    Only the title. Trigrams over a body of prose match almost anything, which
    turns a typo pass into a random-product generator.
    """
    if len(text) < MINIMUM_FUZZY_LENGTH:
        return []

    rows = (
        queryset.filter(title__trigram_similar=text)
        .annotate(rank=TrigramSimilarity("title", text))
        .filter(rank__gte=SIMILARITY_FLOOR)
        .order_by("-rank", "title")[offset : offset + limit]
        .values("object_type", "object_id", "title", "rank")
    )

    return [
        Hit(
            object_type=row["object_type"],
            object_id=row["object_id"],
            title=row["title"],
            rank=float(row["rank"] or 0),
        )
        for row in rows
    ]


def _prefix_query(text: str):
    """
    A `word:*` query for the last token, so a half-typed word still matches.

    Tokens are stripped to letters and digits before being handed to
    `to_tsquery`, which has its own operator syntax: a shopper typing "a & b"
    into a search box is not writing a query language, and passing it through
    raw is both a crash and an injection surface.
    """
    tokens = ["".join(character for character in part if character.isalnum()) for part in text.split()]
    tokens = [token for token in tokens if token]

    if not tokens:
        # Nothing searchable survived. An impossible query beats a query that
        # matches everything.
        return SearchQuery("", config=CONFIG, search_type="plain")

    expression = " & ".join(tokens[:-1] + [f"{tokens[-1]}:*"])

    return SearchQuery(expression, config=CONFIG, search_type="raw")


def _refresh_vectors(queryset: QuerySet) -> None:
    """Rebuilds the stored tsvector for the given documents, in one statement."""
    queryset.update(
        search_vector=SearchVector("title", weight=TITLE_WEIGHT, config=CONFIG)
        + SearchVector("keywords", weight=KEYWORD_WEIGHT, config=CONFIG)
        + SearchVector("body", weight=BODY_WEIGHT, config=CONFIG)
    )
