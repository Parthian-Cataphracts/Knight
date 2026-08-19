"""
Error reporting: what gets sent, what never does, and what happens when there is
more of it than anyone can accept.
"""

from __future__ import annotations

from unittest import mock

from django.test import RequestFactory, SimpleTestCase, override_settings

from knight_integration.errors import scrub
from knight_integration.errors.middleware import KnightErrorReportingMiddleware, build_event
from knight_integration.errors.queue import ErrorReporter

STORE_SETTINGS = {
    "BASE_URL": "http://localhost:5008",
    "CLIENT_ID": "knight-test-0000",
    "CLIENT_SECRET": "secret",
    "ENVIRONMENT": "Development",
    "STORE_VERSION": "1.0.0",
    "ERROR_REPORTING": True,
    "ERROR_BATCH_SIZE": 5,
    "ERROR_QUEUE_LIMIT": 10,
    "ERROR_FLUSH_SECONDS": 60,
}


class ScrubbingTests(SimpleTestCase):
    def test_sensitive_keys_are_replaced_not_dropped(self):
        result = scrub.scrub({"password": "hunter2", "user": "ali"})

        # Replaced rather than removed, so the report still shows the field was there.
        self.assertEqual(scrub.REDACTED, result["password"])
        self.assertEqual("ali", result["user"])

    def test_nested_sensitive_keys_are_reached(self):
        result = scrub.scrub({"outer": {"api_key": "abc", "keep": 1}})

        self.assertEqual(scrub.REDACTED, result["outer"]["api_key"])
        self.assertEqual(1, result["outer"]["keep"])

    def test_only_allowlisted_headers_are_reported(self):
        request = RequestFactory().get(
            "/orders/",
            HTTP_USER_AGENT="curl/8",
            HTTP_AUTHORIZATION="Bearer secret-token",
            HTTP_COOKIE="session=abc",
        )

        context = scrub.describe_request(request)

        self.assertIn("user-agent", context["headers"])
        self.assertNotIn("authorization", context["headers"])
        self.assertNotIn("cookie", context["headers"])

    def test_query_strings_are_reported_by_key_only(self):
        request = RequestFactory().get("/reset/?token=abc123&email=a@b.test")

        context = scrub.describe_request(request)

        self.assertEqual(["email", "token"], context["queryKeys"])
        self.assertNotIn("abc123", str(context))


@override_settings(KNIGHT=STORE_SETTINGS)
class EventBuildingTests(SimpleTestCase):
    def test_an_event_carries_the_endpoint_method_and_trace(self):
        request = RequestFactory().post(
            "/api/orders/",
            HTTP_TRACEPARENT="00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
        )

        try:
            raise RuntimeError("boom")
        except RuntimeError as exc:
            event = build_event(request, exc)

        self.assertEqual("RuntimeError", event["exceptionType"])
        self.assertEqual("/api/orders/", event["endpoint"])
        self.assertEqual("POST", event["httpMethod"])
        self.assertEqual("4bf92f3577b34da6a3ce929d0e0e4736", event["traceId"])
        self.assertIn("boom", event["stackTrace"])

    def test_the_middleware_reports_and_re_raises(self):
        request = RequestFactory().get("/boom/")
        middleware = KnightErrorReportingMiddleware(lambda _: None)

        with mock.patch("knight_integration.errors.middleware.reporter") as reporter:
            result = middleware.process_exception(request, RuntimeError("boom"))

        # None hands the exception straight back to Django: reporting must never
        # change how the store answers.
        self.assertIsNone(result)
        reporter.return_value.enqueue.assert_called_once()

    def test_a_failure_to_report_never_reaches_the_shopper(self):
        request = RequestFactory().get("/boom/")
        middleware = KnightErrorReportingMiddleware(lambda _: None)

        with mock.patch(
            "knight_integration.errors.middleware.reporter",
            side_effect=RuntimeError("the reporter itself broke"),
        ):
            self.assertIsNone(middleware.process_exception(request, RuntimeError("boom")))


@override_settings(KNIGHT=STORE_SETTINGS)
class QueueTests(SimpleTestCase):
    def test_the_queue_drops_the_oldest_events_when_it_is_full(self):
        reporter = ErrorReporter()

        with mock.patch.object(reporter, "_ensure_thread"):
            for index in range(15):
                reporter.enqueue({"message": f"event {index}"})

        # A store in a crash loop generates errors faster than any control plane
        # can accept them; memory is the wrong thing to spend on that.
        self.assertEqual(10, reporter.pending())
        self.assertEqual(5, reporter.dropped)

    def test_flush_sends_in_batches_and_reports_what_was_accepted(self):
        reporter = ErrorReporter()

        with mock.patch.object(reporter, "_ensure_thread"):
            for index in range(7):
                reporter.enqueue({"message": f"event {index}"})

        with mock.patch("knight_integration.client.KnightClient.send_errors") as send:
            send.side_effect = [
                {"accepted": 5, "rejected": 0, "errors": []},
                {"accepted": 2, "rejected": 0, "errors": []},
            ]
            sent = reporter.flush()

        self.assertEqual(7, sent)
        self.assertEqual(2, send.call_count)
        self.assertEqual(0, reporter.pending())

    def test_a_failed_send_does_not_grow_a_backlog_forever(self):
        from knight_integration.client import KnightUnavailable

        reporter = ErrorReporter()

        with mock.patch.object(reporter, "_ensure_thread"):
            for index in range(3):
                reporter.enqueue({"message": f"event {index}"})

        with mock.patch(
            "knight_integration.client.KnightClient.send_errors",
            side_effect=KnightUnavailable("down"),
        ):
            sent = reporter.flush()

        # The batch is dropped rather than requeued: KNIGHT being down must not
        # turn into a backlog that eventually takes the store with it.
        self.assertEqual(0, sent)
        self.assertEqual(0, reporter.pending())
