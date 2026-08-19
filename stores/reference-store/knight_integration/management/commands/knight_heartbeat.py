"""
`manage.py knight_heartbeat` — tell KNIGHT this store is alive.

Complementary to KNIGHT's own polling, not a replacement for it. Polling catches
the store that has stopped; a heartbeat reaches KNIGHT from stores it cannot
call — behind NAT, on customer-managed hosting, anywhere outbound-only
(docs/api-contracts.md §3).

Run it from cron or a systemd timer at the interval KNIGHT asks for, which the
handshake response carries and this command prints.
"""

from __future__ import annotations

from django.core.management.base import BaseCommand, CommandError

from ...client import KnightClient, KnightRejected, KnightUnavailable
from ...features import installed_features
from ...health import checks


class Command(BaseCommand):
    help = "Sends one heartbeat to KNIGHT with this store's current health."

    def add_arguments(self, parser) -> None:
        parser.add_argument(
            "--quiet",
            action="store_true",
            help="Print nothing on success. For cron.",
        )

    def handle(self, *args, **options) -> None:
        status, dependencies = checks.run_all()

        try:
            receipt = KnightClient().heartbeat(
                status=status,
                dependencies=dependencies,
                features=list(installed_features()),
            )
        except (KnightRejected, KnightUnavailable) as exc:
            raise CommandError(f"The heartbeat did not reach KNIGHT: {exc}") from exc

        if options["quiet"]:
            return

        self.stdout.write(
            f"Reported {status}. KNIGHT has this store as {receipt.get('integrationStatus')}; "
            f"next heartbeat in {receipt.get('heartbeatSeconds')}s."
        )

        if receipt.get("domainVerificationOutstanding"):
            self.stdout.write(
                self.style.WARNING("The primary domain is still unproven, so the store stays Pending.")
            )
