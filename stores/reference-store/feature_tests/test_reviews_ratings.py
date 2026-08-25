"""
`reviews-ratings`, installed.

What is worth checking here is not the arithmetic of an average — it is that the
Feature works *as delivered*: its URLs mounted under the prefix its manifest
declares, its template found inside its own package, its static file referenced,
and its moderation default actually defaulting to moderation.
"""

from decimal import Decimal
from unittest import skipUnless

from django.test import TestCase

from feature_tests.support import installed, require

APP = "knight_feature_reviews_ratings"
INSTALLED = installed(APP)
require(APP)

if INSTALLED:  # pragma: no cover - guarded above
    from knight_feature_reviews_ratings import services
    from knight_feature_reviews_ratings.models import Review, ReviewStatus


@skipUnless(INSTALLED, "The reviews-ratings Feature is not installed.")
class ModerationTests(TestCase):
    def test_a_review_arrives_unpublished(self):
        # The first thing an open review box attracts is spam, and a store that
        # removes it afterwards has already shown it to shoppers.
        review = services.submit(1, rating=5, author_name="Sara")

        self.assertEqual(review.status, ReviewStatus.PENDING)
        self.assertIsNone(review.published_at)

    def test_an_unpublished_review_is_not_visible(self):
        services.submit(1, rating=5, author_name="Sara")

        self.assertEqual(services.summary_for(1).count, 0)
        self.assertEqual(services.published_for(1), [])

    def test_publishing_makes_it_visible(self):
        review = services.submit(1, rating=4, author_name="Sara", title="Good")

        self.assertTrue(services.publish(review.pk))

        summary = services.summary_for(1)
        self.assertEqual(summary.count, 1)
        self.assertEqual(summary.average, Decimal("4.0"))
        self.assertEqual(services.published_for(1)[0].title, "Good")

    def test_rejecting_keeps_the_review_and_its_reason(self):
        # A store accused of hiding criticism needs to be able to show what it
        # rejected and why, and a deleted row answers nothing.
        review = services.submit(1, rating=1, author_name="Spam", body="buy pills")

        self.assertTrue(services.reject(review.pk, note="advertising"))

        review.refresh_from_db()
        self.assertEqual(review.status, ReviewStatus.REJECTED)
        self.assertEqual(review.moderator_note, "advertising")
        self.assertEqual(services.summary_for(1).count, 0)

    def test_publishing_something_that_does_not_exist_answers_false(self):
        self.assertFalse(services.publish(999999))
        self.assertFalse(services.reject(999999))

    def test_the_queue_is_oldest_first(self):
        # A queue worked newest-first leaves the earliest reviews unseen for
        # longest, which is the opposite of what a waiting shopper experiences.
        first = services.submit(1, rating=5, author_name="First")
        second = services.submit(1, rating=4, author_name="Second")

        self.assertEqual([r.pk for r in services.pending()], [first.pk, second.pk])


@skipUnless(INSTALLED, "The reviews-ratings Feature is not installed.")
class ValidationTests(TestCase):
    def test_a_rating_outside_one_to_five_is_refused(self):
        for rating in (0, 6, -1):
            with self.subTest(rating=rating):
                with self.assertRaises(Exception):
                    services.submit(1, rating=rating, author_name="Sara")

    def test_a_review_needs_a_name_to_attribute_it_to(self):
        with self.assertRaises(Exception):
            services.submit(1, rating=5, author_name="   ")

    def test_a_signed_in_shopper_reviews_a_product_once(self):
        # Otherwise one account can move a product's average on its own.
        services.submit(1, rating=5, author_name="Sara", shopper_id=42)

        with self.assertRaises(Exception):
            services.submit(1, rating=1, author_name="Sara", shopper_id=42)

    def test_guests_are_not_limited_by_that_constraint(self):
        # The uniqueness is conditional on there being a shopper. A store that
        # allows guest reviews must not be limited to one review per product
        # forever.
        services.submit(1, rating=5, author_name="A")
        services.submit(1, rating=3, author_name="B")

        self.assertEqual(Review.objects.filter(product_id=1).count(), 2)


@skipUnless(INSTALLED, "The reviews-ratings Feature is not installed.")
class SummaryTests(TestCase):
    def _published(self, product_id: int, *ratings: int) -> None:
        for index, rating in enumerate(ratings):
            review = services.submit(
                product_id, rating=rating, author_name=f"Shopper {product_id}-{index}"
            )
            services.publish(review.pk)

    def test_an_unreviewed_product_has_no_average_rather_than_zero(self):
        # Zero is a rating a product could genuinely have, and drawing an
        # unreviewed product as one star is worse than drawing nothing.
        summary = services.summary_for(1)

        self.assertFalse(summary.has_reviews)
        self.assertIsNone(summary.average)

    def test_the_average_is_quantised_to_one_decimal(self):
        # A product page showing 4.333333333 has leaked a float into a design
        # decision.
        self._published(1, 4, 4, 5)

        self.assertEqual(services.summary_for(1).average, Decimal("4.3"))

    def test_the_distribution_covers_every_star(self):
        self._published(1, 5, 5, 3)

        distribution = services.summary_for(1).distribution
        self.assertEqual(distribution, {1: 0, 2: 0, 3: 1, 4: 0, 5: 2})

    def test_several_products_are_summarised_in_one_query(self):
        # The N+1 this exists to prevent is the whole reason a category page
        # would otherwise avoid showing stars at all.
        self._published(1, 5, 5)
        self._published(2, 3)

        with self.assertNumQueries(1):
            summaries = services.summaries_for([1, 2, 3])

        self.assertEqual(summaries[1].count, 2)
        self.assertEqual(summaries[2].average, Decimal("3.0"))
        # Asked for and unreviewed: present, so a caller never has to tell "no
        # reviews" from "I forgot to handle a missing key".
        self.assertEqual(summaries[3].count, 0)

    def test_reads_are_bounded_however_much_is_asked_for(self):
        self._published(1, *([5] * 5))

        self.assertEqual(len(services.published_for(1, limit=1000)), 5)
        self.assertEqual(len(services.published_for(1, limit=2)), 2)


@skipUnless(INSTALLED, "The reviews-ratings Feature is not installed.")
class MerchantReplyTests(TestCase):
    def test_a_reply_is_shown_with_the_review(self):
        review = services.submit(1, rating=2, author_name="Sara", body="Cold when it arrived")
        services.publish(review.pk)

        services.reply(review.pk, "Sorry — we have changed our packaging.")

        self.assertIn("packaging", services.published_for(1)[0].reply)

    def test_replying_twice_replaces_rather_than_appends(self):
        # A merchant correcting their own wording is the common case, and a
        # second reply beneath the first reads as an argument.
        review = services.submit(1, rating=2, author_name="Sara")
        services.publish(review.pk)

        services.reply(review.pk, "First attempt")
        services.reply(review.pk, "Better wording")

        self.assertEqual(services.published_for(1)[0].reply, "Better wording")

    def test_replying_to_something_that_does_not_exist_answers_none(self):
        self.assertIsNone(services.reply(999999, "hello"))


@skipUnless(INSTALLED, "The reviews-ratings Feature is not installed.")
class DeliveredRoutesTests(TestCase):
    """
    The Feature as delivered: mounted, rendering, and serving its own assets.

    These are the assertions that would catch an installer which copied the
    Python and missed the templates — a failure that imports perfectly well and
    then breaks the first product page a shopper opens.
    """

    def test_the_page_is_mounted_under_the_prefix_the_manifest_declares(self):
        response = self.client.get("/reviews/product/1/")

        self.assertEqual(response.status_code, 200)

    def test_the_template_ships_inside_the_package_and_renders(self):
        review = services.submit(1, rating=5, author_name="Sara", title="Excellent")
        services.publish(review.pk)

        response = self.client.get("/reviews/product/1/")
        body = response.content.decode()

        self.assertIn("Excellent", body)
        self.assertIn("Sara", body)
        self.assertIn("5.0", body)

    def test_the_page_references_the_stylesheet_from_the_package(self):
        response = self.client.get("/reviews/product/1/")

        self.assertIn("reviews_ratings/reviews.css", response.content.decode())

    def test_an_unreviewed_product_says_so_rather_than_rendering_empty(self):
        # A blank page reads as the Feature being broken rather than the product
        # being new.
        response = self.client.get("/reviews/product/4242/")

        self.assertIn("No reviews yet", response.content.decode())

    def test_the_json_surface_answers_what_a_storefront_would_fetch(self):
        review = services.submit(1, rating=4, author_name="Sara", title="Good")
        services.publish(review.pk)

        payload = self.client.get("/reviews/product/1/data/").json()

        self.assertEqual(payload["count"], 1)
        self.assertEqual(payload["average"], "4.0")
        self.assertEqual(payload["reviews"][0]["title"], "Good")

    def test_a_submission_comes_back_as_accepted_but_not_visible(self):
        # 202 rather than 201: it exists and nobody can see it yet.
        response = self.client.post(
            "/reviews/product/1/submit/",
            data={"rating": 5, "author": "Sara", "body": "Lovely"},
            content_type="application/json",
        )

        self.assertEqual(response.status_code, 202)
        self.assertEqual(response.json()["status"], ReviewStatus.PENDING)
        self.assertEqual(services.summary_for(1).count, 0)

    def test_a_bad_submission_is_refused_with_a_message(self):
        response = self.client.post(
            "/reviews/product/1/submit/",
            data={"rating": 9, "author": "Sara"},
            content_type="application/json",
        )

        self.assertEqual(response.status_code, 400)
        self.assertIn("error", response.json())

    def test_a_malformed_body_is_refused_rather_than_crashing(self):
        response = self.client.post(
            "/reviews/product/1/submit/", data="not json", content_type="application/json"
        )

        self.assertEqual(response.status_code, 400)

    def test_the_submit_route_takes_post_only(self):
        self.assertEqual(self.client.get("/reviews/product/1/submit/").status_code, 405)


@skipUnless(INSTALLED, "The reviews-ratings Feature is not installed.")
class HealthCheckTests(TestCase):
    def test_the_health_check_passes_on_a_working_install(self):
        from knight_feature_reviews_ratings.checks import health

        self.assertTrue(health())

    def test_the_health_check_fails_when_the_template_is_missing(self):
        # A check that always passes is worse than none: it turns a failed
        # install into a silent one. This is the failure it has to catch —
        # a package whose Python arrived and whose assets did not.
        from unittest.mock import patch

        from knight_feature_reviews_ratings.checks import health

        with patch(
            "django.template.loader.get_template", side_effect=Exception("no such template")
        ):
            self.assertFalse(health())
