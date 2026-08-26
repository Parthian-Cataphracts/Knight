"""
`advanced-search`, installed.

The behaviour worth pinning is not "search finds things". It is the ranking
order, the availability default, what a half-typed word does, and what a query
full of Postgres text-search operators does — because that last one is a crash
and an injection surface if the query is passed through raw.
"""

from unittest import skipUnless

from django.test import TestCase

from feature_tests.support import installed, require

APP = "knight_feature_advanced_search"
INSTALLED = installed(APP)
require(APP)

if INSTALLED:  # pragma: no cover - guarded above
    from knight_feature_advanced_search import services
    from knight_feature_advanced_search.models import SearchDocument


@skipUnless(INSTALLED, "The advanced-search Feature is not installed.")
class IndexingTests(TestCase):
    def test_indexing_the_same_object_twice_keeps_one_document(self):
        # An index that accumulated a row per save would return the same product
        # five times and get slower every time somebody edited it.
        services.index(1, title="Ethiopia Yirgacheffe")
        services.index(1, title="Ethiopia Yirgacheffe (2026 crop)")

        self.assertEqual(SearchDocument.objects.filter(object_id=1).count(), 1)
        self.assertEqual(services.search("2026")[0].object_id, 1)

    def test_a_batch_is_indexed_and_counted(self):
        moved = services.index_many(
            [
                {"object_id": 1, "title": "Coffee"},
                {"object_id": 2, "title": "Tea"},
            ]
        )

        self.assertEqual(moved, 2)
        self.assertEqual(services.stats()["total"], 2)

    def test_removing_a_document_takes_it_out_of_results(self):
        services.index(1, title="Ethiopia Yirgacheffe")

        self.assertTrue(services.remove(1))
        self.assertEqual(services.search("yirgacheffe"), [])

    def test_removing_something_absent_answers_false(self):
        self.assertFalse(services.remove(999999))

    def test_clearing_empties_the_index(self):
        services.index_many([{"object_id": n, "title": f"Product {n}"} for n in range(5)])

        self.assertEqual(services.clear(), 5)
        self.assertEqual(services.stats()["total"], 0)

    def test_an_empty_batch_is_not_an_error(self):
        self.assertEqual(services.index_many([]), 0)


@skipUnless(INSTALLED, "The advanced-search Feature is not installed.")
class RankingTests(TestCase):
    def setUp(self):
        services.index_many(
            [
                {
                    "object_id": 1,
                    "title": "Ethiopia Yirgacheffe",
                    "body": "Bright and floral with notes of bergamot and citrus.",
                    "keywords": "ethiopia yirgacheffe",
                    "category_id": 10,
                },
                {
                    "object_id": 2,
                    "title": "Earl Grey",
                    "body": "Black tea scented with bergamot oil.",
                    "category_id": 20,
                },
                {
                    "object_id": 3,
                    "title": "Sumatra Mandheling",
                    "body": "Dark and earthy with a heavy chocolate finish.",
                    "category_id": 10,
                },
            ]
        )

    def test_a_title_match_outranks_a_body_match(self):
        # The whole reason the vector is weighted. A shopper searching a product
        # name expects that product first, not something whose description
        # mentions it in passing.
        hits = services.search("bergamot")
        by_body = {hit.object_id for hit in hits}
        self.assertEqual(by_body, {1, 2})

        earl = services.search("earl")
        self.assertEqual(earl[0].object_id, 2)
        self.assertGreater(earl[0].rank, hits[0].rank)

    def test_a_word_only_in_the_body_is_still_found(self):
        self.assertEqual([hit.object_id for hit in services.search("chocolate")], [3])

    def test_results_are_bounded_however_much_is_asked_for(self):
        services.index_many([{"object_id": 100 + n, "title": "coffee sample"} for n in range(30)])

        self.assertLessEqual(len(services.search("coffee", limit=1000)), 100)
        self.assertEqual(len(services.search("coffee", limit=2)), 2)

    def test_a_category_filter_narrows_the_results(self):
        hits = services.search("bergamot", category_id=20)

        self.assertEqual([hit.object_id for hit in hits], [2])

    def test_an_empty_query_returns_nothing_rather_than_everything(self):
        # A blank search box must not become "select *".
        self.assertEqual(services.search(""), [])
        self.assertEqual(services.search("   "), [])

    def test_a_query_matching_nothing_returns_nothing(self):
        self.assertEqual(services.search("zzzzzzzz"), [])


@skipUnless(INSTALLED, "The advanced-search Feature is not installed.")
class PartialWordTests(TestCase):
    def setUp(self):
        services.index(1, title="Ethiopia Yirgacheffe", body="Bright and floral.")

    def test_a_half_typed_word_still_matches(self):
        # Full-text needs whole words, so a shopper mid-word has matched nothing
        # and a search box that goes blank reads as broken.
        self.assertEqual([hit.object_id for hit in services.search("yirg")], [1])

    def test_the_prefix_pass_does_not_dilute_a_complete_query(self):
        services.index(2, title="Yirgacheffe blend")

        # "ethiopia" matches document 1 outright, so the prefix pass never runs
        # and document 2 does not appear alongside it.
        self.assertEqual([hit.object_id for hit in services.search("ethiopia")], [1])


@skipUnless(INSTALLED, "The advanced-search Feature is not installed.")
class AvailabilityTests(TestCase):
    def setUp(self):
        services.index(1, title="Ethiopia Yirgacheffe", is_available=True)
        services.index(2, title="Decaf Brazil", is_available=False)

    def test_unavailable_documents_are_hidden_by_default(self):
        # A shop must not offer what it cannot sell, and the caller who forgets
        # is the storefront.
        self.assertEqual([hit.object_id for hit in services.search("brazil")], [])

    def test_they_can_be_asked_for_explicitly(self):
        # A merchant searching their own catalogue needs to find the draft they
        # are about to publish.
        hits = services.search("brazil", include_unavailable=True)

        self.assertEqual([hit.object_id for hit in hits], [2])

    def test_suggestions_never_offer_something_unavailable(self):
        self.assertEqual(services.suggest("dec"), [])
        self.assertEqual(services.suggest("eth"), ["Ethiopia Yirgacheffe"])


@skipUnless(INSTALLED, "The advanced-search Feature is not installed.")
class SuggestionTests(TestCase):
    def setUp(self):
        services.index_many(
            [
                {"object_id": 1, "title": "Earl Grey"},
                {"object_id": 2, "title": "Ethiopia Yirgacheffe"},
            ]
        )

    def test_one_character_suggests_nothing(self):
        # Every title in the catalogue is not a suggestion list.
        self.assertEqual(services.suggest("e"), [])

    def test_two_characters_suggest(self):
        self.assertEqual(services.suggest("ea"), ["Earl Grey"])

    def test_suggestions_are_case_insensitive(self):
        self.assertEqual(services.suggest("EARL"), ["Earl Grey"])


@skipUnless(INSTALLED, "The advanced-search Feature is not installed.")
class HostileQueryTests(TestCase):
    """
    A shopper typing into a search box is not writing a query language.

    `to_tsquery` has its own operator syntax, and passing a raw string to it is
    both a crash and an injection surface. Every one of these must come back as
    an ordinary empty result rather than an exception or a dropped table.
    """

    QUERIES = (
        "!(",
        "coffee | tea & !x",
        "'; drop table knight_search_document; --",
        ":*",
        "<>",
        "&&&",
        "\\",
        "a" * 300,
        "tea:*:*",
    )

    def setUp(self):
        services.index(1, title="Earl Grey", body="Black tea scented with bergamot oil.")

    def test_none_of_them_raise(self):
        for query in self.QUERIES:
            with self.subTest(query=query[:24]):
                services.search(query)

    def test_the_index_survives_all_of_them(self):
        for query in self.QUERIES:
            services.search(query)

        self.assertEqual(services.stats()["total"], 1)

    def test_operators_are_stripped_rather_than_honoured(self):
        # `tea:*:*` is a prefix search for "tea", not a syntax error and not a
        # query the shopper meant to write.
        self.assertEqual([hit.object_id for hit in services.search("tea:*:*")], [1])


@skipUnless(INSTALLED, "The advanced-search Feature is not installed.")
class FacetTests(TestCase):
    def setUp(self):
        services.index_many(
            [
                {"object_id": 1, "title": "Coffee one", "category_id": 10},
                {"object_id": 2, "title": "Coffee two", "category_id": 10},
                {"object_id": 3, "title": "Tea one", "category_id": 20},
            ]
        )

    def test_facets_count_only_what_matches_the_query(self):
        # A facet that ignores the query offers filters that lead to nothing.
        counts = services.facets("coffee")
        categories = {facet.value: facet.count for facet in counts["categoryId"]}

        self.assertEqual(categories, {10: 2})

    def test_facets_with_no_query_cover_the_whole_index(self):
        counts = services.facets("")
        categories = {facet.value: facet.count for facet in counts["categoryId"]}

        self.assertEqual(categories, {10: 2, 20: 1})


@skipUnless(INSTALLED, "The advanced-search Feature is not installed.")
class DeliveredRoutesTests(TestCase):
    def setUp(self):
        services.index(1, title="Ethiopia Yirgacheffe", body="Bright and floral.")

    def test_the_search_route_is_mounted_under_the_declared_prefix(self):
        payload = self.client.get("/search/?q=yirgacheffe").json()

        self.assertEqual(payload["count"], 1)
        self.assertEqual(payload["results"][0]["title"], "Ethiopia Yirgacheffe")

    def test_the_facets_route_answers(self):
        self.assertIn("facets", self.client.get("/search/facets/?q=ethiopia").json())

    def test_the_suggest_route_answers(self):
        payload = self.client.get("/search/suggest/?q=eth").json()

        self.assertEqual(payload["suggestions"], ["Ethiopia Yirgacheffe"])

    def test_the_status_route_reports_what_is_indexed(self):
        # For an operator asking whether a reindex actually ran, which is
        # otherwise a question only the database can answer.
        payload = self.client.get("/search/status/").json()

        self.assertEqual(payload["indexed"]["total"], 1)

    def test_a_hostile_query_over_http_is_an_empty_result_not_a_500(self):
        response = self.client.get("/search/?q=%21%28")

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.json()["count"], 0)


@skipUnless(INSTALLED, "The advanced-search Feature is not installed.")
class HealthCheckTests(TestCase):
    def test_the_health_check_passes_on_a_working_install(self):
        from knight_feature_advanced_search.checks import health

        self.assertTrue(health())

    def test_the_health_check_fails_when_a_query_cannot_run(self):
        # The thing most likely to be wrong after installing a search feature is
        # the GIN index or the tsvector column, and neither shows up in an
        # import. A check that always passed would turn a failed install into a
        # silent one.
        from unittest.mock import patch

        from knight_feature_advanced_search.checks import health

        with patch(
            "knight_feature_advanced_search.services.search",
            side_effect=Exception("relation does not exist"),
        ):
            self.assertFalse(health())


class TheStoreIndexesItsOwnCatalogueTests(TestCase):
    """
    The seam, from the store's side. Runs whether or not the Feature is present,
    because the command has to behave either way.
    """

    def test_the_reindex_command_reports_rather_than_failing(self):
        from io import StringIO

        from django.core.management import call_command

        out = StringIO()
        call_command("knight_reindex_search", stdout=out)
        output = out.getvalue()

        self.assertTrue(
            "nothing to index" in output or "Indexed" in output,
            f"The command finished without saying what it did: {output!r}",
        )

    @skipUnless(INSTALLED, "The advanced-search Feature is not installed.")
    def test_it_indexes_products_and_marks_drafts_unavailable(self):
        from decimal import Decimal
        from io import StringIO

        from django.core.management import call_command

        from apps.catalog.models import Category, Product

        category = Category.objects.create(name="Coffee", slug="coffee")
        Product.objects.create(
            name="Ethiopia Yirgacheffe",
            slug="ethiopia-yirgacheffe",
            category=category,
            status="Active",
            base_price=Decimal("420000"),
        )
        Product.objects.create(
            name="Decaf Brazil",
            slug="decaf-brazil",
            category=category,
            status="Draft",
            base_price=Decimal("380000"),
        )

        call_command("knight_reindex_search", "--rebuild", stdout=StringIO())

        # Both indexed; only the active one is offered to a shopper.
        self.assertEqual(services.stats()["total"], 2)
        self.assertEqual(len(services.search("brazil")), 0)
        self.assertEqual(len(services.search("brazil", include_unavailable=True)), 1)

    @skipUnless(INSTALLED, "The advanced-search Feature is not installed.")
    def test_the_slug_is_indexed_as_keywords(self):
        # A shopper often half-remembers the URL rather than the product name,
        # and the slug's words are not in the title once its hyphens are gone.
        from decimal import Decimal
        from io import StringIO

        from django.core.management import call_command

        from apps.catalog.models import Category, Product

        category = Category.objects.create(name="Coffee", slug="coffee")
        Product.objects.create(
            name="House Blend",
            slug="everyday-filter-coffee",
            category=category,
            status="Active",
            base_price=Decimal("200000"),
        )

        call_command("knight_reindex_search", "--rebuild", stdout=StringIO())

        self.assertEqual(len(services.search("everyday filter")), 1)
