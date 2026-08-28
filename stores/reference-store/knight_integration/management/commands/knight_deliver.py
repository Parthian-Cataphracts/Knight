"""
Sends the events waiting for a Feature's service.

`manage.py knight_deliver`, on a timer. A command rather than a daemon for the
same reason every other agent command in this package is one: it exits with a
status, writes to a log, and can be run by hand during an incident to see exactly
what happens.

Safe to run twice at once. Each delivery is locked while it is attempted, so the
obvious way to catch up after an outage - start a second worker - does not send
everything twice.
"""

from __future__ import annotations

from django.core.management.base import BaseCommand

from knight_integration.external import delivery


class Command(BaseCommand):
    help = "Deliver queued events to the services that subscribed to them."

    def add_arguments(self, parser) -> None:
        parser.add_argument(
            "--limit",
            type=int,
            default=100,
            help="How many deliveries to attempt in one pass.",
        )
        parser.add_argument(
            "--dead-letters",
            action="store_true",
            help="Show what has been given up on, and send nothing.",
        )

    def handle(self, *args, **options) -> None:
        if options["dead_letters"]:
            self._dead_letters()
            return

        counts = delivery.send_due(limit=options["limit"])

        self.stdout.write(
            f"{counts['delivered']} delivered, {counts['retrying']} retrying, "
            f"{counts['dead']} dead-lettered."
        )

        if counts["dead"]:
            # Loud, because a dead letter is a Feature a merchant pays for not
            # having heard something. It is not a tidy-up.
            self.stdout.write(
                self.style.ERROR(
                    f"{counts['dead']} delivery(ies) were given up on. "
                    "Run with --dead-letters to see them."
                )
            )

    def _dead_letters(self) -> None:
        found = delivery.WebhookDelivery.objects.filter(state=delivery.DeliveryState.DEAD)

        if not found.exists():
            self.stdout.write("Nothing has been given up on.")
            return

        for row in found.order_by("-created_at")[:100]:
            self.stdout.write(
                f"  {row.created_at:%Y-%m-%d %H:%M}  {row.event:<24} -> {row.feature_slug:<24} "
                f"after {row.attempts} attempt(s): {row.last_error or row.last_status}"
            )

        self.stdout.write(f"\n{found.count()} dead-lettered delivery(ies).")
