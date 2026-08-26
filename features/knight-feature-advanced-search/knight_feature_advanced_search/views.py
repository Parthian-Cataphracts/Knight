"""
The search endpoints.

JSON only. Unlike reviews, this Feature has no page of its own to render: search
results belong on the store's own listing page, laid out the way that store lays
things out. A Feature that shipped its own results page would be a Feature
imposing a design on every store it lands on.
"""

from __future__ import annotations

from django.http import JsonResponse

from . import services


def search(request):
    """Ranked results, with the filters a listing page needs."""
    query = request.GET.get("q", "")

    hits = services.search(
        query,
        object_type=request.GET.get("type") or None,
        category_id=_int(request.GET.get("category")),
        include_unavailable=request.GET.get("includeUnavailable") == "true",
        limit=_int(request.GET.get("limit")) or 20,
        offset=_int(request.GET.get("offset")) or 0,
    )

    return JsonResponse(
        {
            "query": query,
            "count": len(hits),
            "results": [
                {
                    "type": hit.object_type,
                    "id": hit.object_id,
                    "title": hit.title,
                    "score": str(hit.score),
                }
                for hit in hits
            ],
        }
    )


def facets(request):
    """Filter counts for the current query."""
    query = request.GET.get("q", "")
    computed = services.facets(query, object_type=request.GET.get("type") or None)

    return JsonResponse(
        {
            "query": query,
            "facets": {
                name: [{"value": facet.value, "count": facet.count} for facet in values]
                for name, values in computed.items()
            },
        }
    )


def suggest(request):
    """Titles beginning with what has been typed."""
    return JsonResponse({"suggestions": services.suggest(request.GET.get("q", ""))})


def status(request):
    """
    What is indexed. For an operator asking whether a reindex actually ran,
    which is otherwise a question only the database can answer.
    """
    return JsonResponse({"indexed": services.stats()})


def _int(value) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None
