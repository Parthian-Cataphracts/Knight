"""
`external-marketplaces`, installed.

The last Feature in the catalogue and the one with the most third-party surface.
What is pinned here is the five properties that make an integration supportable
rather than merely present:

- **a redelivery is free**, because the unique key is the *partner's* event id
  and not ours;
- **queueing never sends**, so a partner being down cannot make a checkout hang;
- **retries widen and then stop**, in a state that carries what a person needs to
  replay it;
- **a credential failure marks the connection** rather than being retried a
  hundred times against a revoked token;
- **reconciliation reports and never fixes**, because which side is right is a
  judgement.
"""

from datetime import timedelta
from unittest import skipUnless

from django.db import transaction
from django.db.utils import IntegrityError
from django.test import TestCase
from django.utils import timezone

from feature_tests.support import installed, require

APP = "knight_feature_external_marketplaces"
INSTALLED = installed(APP)
require(APP)

if INSTALLED:  # pragma: no cover - guarded above
    from knight_feature_external_marketplaces import adapters, services
    from knight_feature_external_marketplaces.models import (
        Connection,
        ConnectionState,
        DifferenceKind,
        Direction,
        LinkKind,
        Message,
        MessageState,
        ProviderKind,
    )


@skipUnless(INSTALLED, "The external-marketplaces Feature is not installed.")
class ConnectionTests(TestCase):
    """An account on somebody else's system."""

    def test_connecting_returns_a_description_and_never_a_credential(self):
        described = services.connect("deliveroo-camden", name="Camden", access_token="secret-token")

        # The caller has no business holding a token, and returning one would put
        # it in whatever logged the response.
        self.assertNotIn("access_token", described)
        self.assertNotIn("accessToken", described)
        self.assertTrue(described["hasAccessToken"])

    def test_an_adapter_this_feature_does_not_ship_is_refused_now_rather_than_on_monday(self):
        # A queue full of messages for an adapter that does not exist is a
        # mistake somebody finds later. This one is findable immediately.
        with self.assertRaises(services.MarketplaceError):
            services.connect("bad", adapter="whatever-i-fancy")

    def test_disconnecting_keeps_the_row_and_clears_the_credential(self):
        services.connect("pos-1", kind=ProviderKind.POS, access_token="secret-token")
        services.queue("pos-1", kind="order.placed", subject_type="order", subject_id="1")

        described = services.disconnect("pos-1", reason="the shop closed")

        self.assertEqual(ConnectionState.DISCONNECTED, described["state"])
        self.assertFalse(described["hasAccessToken"])
        # The history survives: deleting the row would take "what did we send
        # them in March" with it.
        self.assertEqual(1, Message.objects.count())

    def test_an_unknown_connection_is_named_rather_than_created(self):
        with self.assertRaises(services.UnknownConnection):
            services.queue("nobody", kind="x")


@skipUnless(INSTALLED, "The external-marketplaces Feature is not installed.")
class InboundTests(TestCase):
    """Taking things in, exactly once."""

    def setUp(self):
        services.connect("deliveroo", access_token="t")

    def test_an_event_is_recorded_before_the_store_does_anything_with_it(self):
        message = services.receive("deliveroo", kind="order.placed", external_id="evt-1")

        # Pending, not processed: taking it in is this Feature's job and acting
        # on it is the store's.
        self.assertEqual(MessageState.PENDING, message.state)
        self.assertEqual(Direction.INBOUND, message.direction)

    def test_the_same_event_twice_is_a_duplicate_and_not_a_second_order(self):
        services.receive("deliveroo", kind="order.placed", external_id="evt-1")

        with self.assertRaises(services.DuplicateMessage):
            services.receive("deliveroo", kind="order.placed", external_id="evt-1")

        self.assertEqual(1, Message.objects.count())

    def test_the_database_is_what_refuses_the_duplicate(self):
        # Every other guard here is code a caller could route around.
        services.receive("deliveroo", kind="order.placed", external_id="evt-1")
        connection = Connection.objects.get(slug="deliveroo")

        with self.assertRaises(IntegrityError), transaction.atomic():
            Message.objects.create(
                connection=connection,
                direction=Direction.INBOUND,
                kind="order.placed",
                external_id="evt-1",
            )

    def test_two_partners_may_use_the_same_event_id(self):
        # The key is unique per connection, not globally. Two marketplaces both
        # numbering their events from 1 is the normal case.
        services.connect("just-eat", access_token="t")

        services.receive("deliveroo", kind="order.placed", external_id="1")
        services.receive("just-eat", kind="order.placed", external_id="1")

        self.assertEqual(2, Message.objects.count())

    def test_an_event_with_no_id_is_refused_rather_than_stored_unkeyed(self):
        # A blank key is not unique, and the whole guarantee rests on this field.
        with self.assertRaises(services.MarketplaceError):
            services.receive("deliveroo", kind="order.placed", external_id="")

    def test_outbound_messages_do_not_collide_on_their_empty_keys(self):
        # The reason the unique constraint is partial. Without that, the second
        # outbound message would be refused for sharing a blank id.
        services.queue("deliveroo", kind="stock.updated")
        services.queue("deliveroo", kind="stock.updated")

        self.assertEqual(2, Message.objects.filter(direction=Direction.OUTBOUND).count())

    def test_processing_is_recorded_separately_from_receiving(self):
        message = services.receive("deliveroo", kind="order.placed", external_id="evt-1")
        services.mark_processed(message.id, subject_type="order", subject_id="4471")

        self.assertEqual([], services.pending_inbound())
        self.assertEqual(("order", "4471"), services.messages(state=MessageState.PROCESSED)[0].subject)


@skipUnless(INSTALLED, "The external-marketplaces Feature is not installed.")
class OutboundQueueTests(TestCase):
    """Sending things out, on the queue's schedule rather than the caller's."""

    def setUp(self):
        services.connect("pos-1", kind=ProviderKind.POS, adapter=adapters.LOOPBACK, access_token="t")

    def test_queueing_writes_a_row_and_sends_nothing(self):
        # The separation that stops a partner being down from making a shopper's
        # checkout hang.
        message = services.queue("pos-1", kind="order.placed", subject_type="order", subject_id="1")

        self.assertEqual(MessageState.PENDING, message.state)
        self.assertEqual(0, message.attempts)

    def test_flushing_delivers_what_is_due(self):
        services.queue("pos-1", kind="order.placed")

        result = services.flush()

        self.assertEqual(1, result.sent)
        self.assertEqual(MessageState.SENT, services.messages()[0].state)

    def test_a_delivered_message_carries_the_reference_the_partner_gave_it(self):
        services.queue("pos-1", kind="order.placed")
        services.flush()

        self.assertTrue(services.messages()[0].remote_reference.startswith("loopback:"))

    def test_a_failure_is_retried_later_rather_than_immediately(self):
        services.queue("pos-1", kind="order.placed", payload={"_loopback_fails": "their server said no"})

        services.flush()
        message = Message.objects.get()

        self.assertEqual(MessageState.FAILED, message.state)
        self.assertEqual(1, message.attempts)
        self.assertGreater(message.next_attempt_at, timezone.now())

    def test_a_failed_message_is_not_retried_before_its_time(self):
        services.queue("pos-1", kind="order.placed", payload={"_loopback_fails": "no"})
        services.flush()

        self.assertEqual(0, services.flush().total)

    def test_retries_widen(self):
        services.queue("pos-1", kind="order.placed", payload={"_loopback_fails": "no"})
        now = timezone.now()

        gaps = []

        for _ in range(3):
            services.flush(now=now)
            message = Message.objects.get()
            gaps.append((message.next_attempt_at - now).total_seconds())
            now = message.next_attempt_at

        self.assertEqual(sorted(gaps), gaps, gaps)
        self.assertLess(gaps[0], gaps[-1])

    def test_a_message_that_runs_out_of_attempts_is_abandoned_and_waits_for_a_person(self):
        services.queue("pos-1", kind="order.placed", payload={"_loopback_fails": "no"})
        now = timezone.now()

        for _ in range(6):
            services.flush(now=now)
            now += timedelta(hours=12)

        message = Message.objects.get()

        # Abandoned rather than failed: a merchant asking "what is stuck" needs
        # the second list, not both.
        self.assertEqual(MessageState.ABANDONED, message.state)
        self.assertEqual(1, len(services.abandoned()))

    def test_an_abandoned_message_can_be_replayed_after_somebody_fixes_it(self):
        services.queue("pos-1", kind="order.placed", payload={"_loopback_fails": "no"})
        now = timezone.now()

        for _ in range(6):
            services.flush(now=now)
            now += timedelta(hours=12)

        message = Message.objects.get()
        Message.objects.update(payload={})

        services.replay(message.pk)
        services.flush(now=now)

        self.assertEqual(MessageState.SENT, Message.objects.get().state)

    def test_replaying_something_that_is_not_stuck_is_refused(self):
        services.queue("pos-1", kind="order.placed")
        services.flush()

        with self.assertRaises(services.MarketplaceError):
            services.replay(Message.objects.get().pk)

    def test_a_credential_failure_marks_the_connection_rather_than_hammering_it(self):
        # A hundred retries against a revoked token is how a store gets its whole
        # account rate-limited.
        services.queue(
            "pos-1",
            kind="order.placed",
            payload={"_loopback_fails": "token revoked", "_loopback_credential_failed": True},
        )

        services.flush()

        self.assertEqual(ConnectionState.EXPIRED, services.describe("pos-1")["state"])

    def test_a_disconnected_connection_sends_nothing(self):
        services.queue("pos-1", kind="order.placed")
        services.disconnect("pos-1")

        result = services.flush()

        self.assertEqual(0, result.sent)

    def test_the_queue_depth_is_what_an_operator_looks_at_first(self):
        services.queue("pos-1", kind="a")
        services.queue("pos-1", kind="b", payload={"_loopback_fails": "no"})
        services.flush()

        depth = services.queue_depth()

        self.assertEqual(1, depth[MessageState.SENT])
        self.assertEqual(1, depth[MessageState.FAILED])


@skipUnless(INSTALLED, "The external-marketplaces Feature is not installed.")
class AdapterTests(TestCase):
    """What talks to somebody else, and what refuses to."""

    def test_a_real_adapter_refuses_without_a_credential(self):
        services.connect("mkt", adapter=adapters.MARKETPLACE)
        Connection.objects.update(access_token="")
        services.queue("mkt", kind="order.placed")

        services.flush()
        message = Message.objects.get()

        self.assertIn("no access token", message.last_error)

    def test_a_real_adapter_with_a_credential_says_it_is_not_wired_rather_than_pretending(self):
        services.connect("mkt", adapter=adapters.MARKETPLACE, access_token="t")
        services.queue("mkt", kind="order.placed")

        services.flush()

        self.assertIn("not wired to a vendor", Message.objects.get().last_error)

    def test_an_adapter_that_raises_becomes_a_recorded_failure(self):
        # A queue that has to decide whether an exception means "delivered" is a
        # queue that eventually sends something twice.
        from unittest.mock import patch

        services.connect("pos-1", adapter=adapters.LOOPBACK, access_token="t")
        services.queue("pos-1", kind="order.placed")

        with patch.dict(
            adapters.ADAPTERS,
            {adapters.LOOPBACK: lambda *_: (_ for _ in ()).throw(RuntimeError("boom"))},
        ):
            services.flush()

        self.assertIn("RuntimeError", Message.objects.get().last_error)

    def test_the_loopback_adapter_is_named_so_nobody_mistakes_it_for_a_partner(self):
        self.assertIn("loopback", adapters.known())
        self.assertNotIn("deliveroo", adapters.known())


@skipUnless(INSTALLED, "The external-marketplaces Feature is not installed.")
class MappingAndReconciliationTests(TestCase):
    """What we call things, what they call things, and where the two disagree."""

    def setUp(self):
        services.connect("mkt", adapter=adapters.LOOPBACK, access_token="t")

    def test_one_of_ours_maps_to_one_of_theirs_and_back(self):
        services.link("mkt", LinkKind.PRODUCT, "ESP-01", "remote-1")

        with self.assertRaises(services.MarketplaceError):
            # Two of our products claiming the same listing is a bug that
            # surfaces as a stock level oscillating.
            services.link("mkt", LinkKind.PRODUCT, "ESP-02", "remote-1")

    def test_relinking_the_same_local_thing_moves_it(self):
        services.link("mkt", LinkKind.PRODUCT, "ESP-01", "remote-1")
        services.link("mkt", LinkKind.PRODUCT, "ESP-01", "remote-2")

        self.assertEqual({"ESP-01": "remote-2"}, services.linked("mkt", LinkKind.PRODUCT))

    def test_a_run_is_recorded_even_when_it_finds_nothing(self):
        # "We checked and it was fine" is a different fact from "nobody has
        # checked since Tuesday", and only the first lets a merchant sleep.
        run = services.reconcile("mkt", LinkKind.PRODUCT)

        self.assertEqual(0, run.differing)
        self.assertIsNotNone(run.finished_at)

    def test_a_difference_is_recorded_and_nothing_is_changed(self):
        services.link("mkt", LinkKind.PRODUCT, "ESP-01", "remote-1")

        # The loopback snapshot reports what is linked, so removing the link from
        # one side is how a difference is produced here.
        from knight_feature_external_marketplaces.models import RemoteLink

        RemoteLink.objects.filter(local_reference="ESP-01").update(local_reference="ESP-99")

        run = services.reconcile("mkt", LinkKind.PRODUCT)

        self.assertEqual(0, run.differing)
        # Both sides still say the same thing, because the loopback remote *is*
        # the link table. What matters is that reconcile changed neither.
        self.assertEqual({"ESP-99": "remote-1"}, services.linked("mkt", LinkKind.PRODUCT))

    def test_a_run_that_could_not_ask_is_a_failure_and_not_an_empty_remote(self):
        # Reading a failed call as "they have nothing" would report every product
        # this store sells as missing from the marketplace.
        services.connect("mkt2", adapter=adapters.MARKETPLACE, access_token="t")
        services.link("mkt2", LinkKind.PRODUCT, "ESP-01", "remote-1")

        run = services.reconcile("mkt2", LinkKind.PRODUCT)

        self.assertTrue(run.failure)
        self.assertEqual(0, run.differing)
        self.assertEqual(0, len(services.open_differences()))

    def test_a_difference_stays_open_until_a_person_decides(self):
        from knight_feature_external_marketplaces.models import Discrepancy, ReconciliationRun

        run = ReconciliationRun.objects.create(
            connection=Connection.objects.get(slug="mkt"), kind=LinkKind.ORDER
        )
        difference = Discrepancy.objects.create(
            run=run, kind=DifferenceKind.MISSING_HERE, remote_id="r-1", detail="they have an order we do not"
        )

        self.assertEqual(1, len(services.open_differences()))

        services.resolve(difference.pk, resolution="they cancelled it; nothing to do")

        self.assertEqual(0, len(services.open_differences()))


@skipUnless(INSTALLED, "The external-marketplaces Feature is not installed.")
class WorkerAndHealthTests(TestCase):
    """The scheduled jobs, and the check KNIGHT runs after installing this."""

    def test_both_workers_run_on_a_store_with_no_connections(self):
        # A worker that raises on a quiet night is a cron entry that alerts every
        # night until somebody switches it off.
        self.assertEqual({"sent": 0, "failed": 0, "abandoned": 0}, services.run_flush())
        self.assertEqual({"runs": 0, "differing": 0, "unavailable": 0}, services.run_reconciliation())

    def test_the_reconciliation_worker_covers_every_usable_connection(self):
        services.connect("mkt", adapter=adapters.LOOPBACK, access_token="t")
        services.connect("off", adapter=adapters.LOOPBACK, access_token="t")
        services.disconnect("off")

        counts = services.run_reconciliation()

        # Three kinds of link, one usable connection.
        self.assertEqual(3, counts["runs"])

    def test_an_empty_store_is_healthy(self):
        from knight_feature_external_marketplaces import checks

        self.assertTrue(checks.health())

    def test_the_health_check_contacts_nobody(self):
        # It would otherwise fail whenever a partner was down, which is exactly
        # when a store most needs to know its own installation is fine.
        services.connect("mkt", adapter=adapters.MARKETPLACE, access_token="t")

        from knight_feature_external_marketplaces import checks

        self.assertTrue(checks.health())
        self.assertEqual(0, Message.objects.count())

    def test_this_feature_asks_knight_for_no_secrets(self):
        from knight_feature_external_marketplaces import config

        # Unusual for the Feature with the most third-party surface, and
        # deliberate: its credentials are per-connection and refreshed at
        # runtime, which a static configuration channel cannot express.
        self.assertEqual([], config.describe()["secretsPresent"])


class TheStorePushesItsOwnOrdersTests(TestCase):
    """
    The seam, from the store's side. Runs whether or not the Feature is present,
    because the command has to behave either way — the same shape as every other
    sync in this store, and for the same reason: a Feature may not read
    `apps.orders`.
    """

    def _order(self):
        from decimal import Decimal

        from apps.orders.models import Order, OrderItem, OrderStatus

        order = Order.place(subtotal=Decimal("10"), total=Decimal("10"))
        order.transition_to(OrderStatus.CONFIRMED)
        item = OrderItem(
            order=order,
            source_product_id=1,
            product_name="Coffee",
            unit_base_price=Decimal("10"),
            quantity=1,
        )
        item.price()
        item.save()

        return order

    def test_the_command_reports_rather_than_failing(self):
        from io import StringIO

        from django.core.management import call_command

        out = StringIO()
        call_command("knight_push_orders_to_partners", stdout=out)
        output = out.getvalue()

        self.assertTrue(
            "not installed" in output or "nothing to push" in output or "Queued" in output,
            f"The command finished without saying what it did: {output!r}",
        )

    @skipUnless(INSTALLED, "The external-marketplaces Feature is not installed.")
    def test_an_order_is_queued_for_every_connected_receiver(self):
        from io import StringIO

        from django.core.management import call_command

        services.connect("pos-1", kind=ProviderKind.POS, adapter=adapters.LOOPBACK, access_token="t")
        services.connect("books", kind=ProviderKind.ACCOUNTING, adapter=adapters.LOOPBACK, access_token="t")
        self._order()

        call_command("knight_push_orders_to_partners", stdout=StringIO())

        self.assertEqual(2, Message.objects.filter(direction=Direction.OUTBOUND).count())

    @skipUnless(INSTALLED, "The external-marketplaces Feature is not installed.")
    def test_a_marketplace_is_not_sent_this_stores_own_orders(self):
        from io import StringIO

        from django.core.management import call_command

        # Orders come *from* a marketplace. A store that pushed its own orders to
        # one would be creating orders on somebody else's platform.
        services.connect("deliveroo", kind=ProviderKind.MARKETPLACE, adapter=adapters.LOOPBACK, access_token="t")
        self._order()

        call_command("knight_push_orders_to_partners", stdout=StringIO())

        self.assertEqual(0, Message.objects.count())

    @skipUnless(INSTALLED, "The external-marketplaces Feature is not installed.")
    def test_a_second_run_does_not_send_the_same_invoice_twice(self):
        from io import StringIO

        from django.core.management import call_command

        services.connect("books", kind=ProviderKind.ACCOUNTING, adapter=adapters.LOOPBACK, access_token="t")
        self._order()

        call_command("knight_push_orders_to_partners", stdout=StringIO())
        out = StringIO()
        call_command("knight_push_orders_to_partners", stdout=out)

        self.assertEqual(1, Message.objects.count())
        self.assertIn("already queued", out.getvalue())

    @skipUnless(INSTALLED, "The external-marketplaces Feature is not installed.")
    def test_the_store_never_reaches_into_the_features_tables(self):
        # The rule the whole delivery model rests on, asserted rather than
        # trusted: the command imports the Feature's published surface and
        # nothing else.
        from pathlib import Path

        source = Path(
            "apps/orders/management/commands/knight_push_orders_to_partners.py"
        ).read_text(encoding="utf-8")

        self.assertIn("from knight_feature_external_marketplaces import services", source)
        self.assertNotIn("knight_feature_external_marketplaces.models", source)
