"""
`manage.py knight_sync_features` — pull the entitlement set and cache it.

Run on a schedule (cron, systemd timer, the deployment pipeline), and by hand
after a plan change when somebody does not want to wait for the TTL. It is also
the honest way to see what this store believes it is entitled to, and where that
belief came from.
"""

from __future__ import annotations

from django.core.management.base import BaseCommand, CommandError

from ...client import KnightRejected, KnightUnavailable
from ...features import current, installed_features, refresh


class Command(BaseCommand):
    help = "Refreshes this store's entitlement set from KNIGHT."

    def add_arguments(self, parser) -> None:
        parser.add_argument(
            "--offline",
            action="store_true",
            help="Report what is cached without calling KNIGHT.",
        )

    def handle(self, *args, **options) -> None:
        if options["offline"]:
            self._report(current())
            return

        try:
            self._report(refresh())
        except (KnightUnavailable, KnightRejected) as exc:
            # Deliberately not a silent fallback: the point of running this is to
            # learn whether the refresh worked.
            raise CommandError(f"Could not refresh entitlements: {exc}") from exc
        except ValueError as exc:
            raise CommandError(
                f"{exc} This usually means the store's signing key is stale — run `knight_register --force` first."
            ) from exc

    def _report(self, entitlements) -> None:
        installed = set(installed_features())
        entitled = set(entitlements.slugs)

        self.stdout.write(f"Entitlement set ({entitlements.source}):")

        for slug in sorted(entitled | installed):
            if slug in entitled and slug in installed:
                self.stdout.write(self.style.SUCCESS(f"  {slug}: entitled and installed"))
            elif slug in entitled:
                # The delivery gap: paid for, not present. Phase 3.5 turns this
                # into an installation job; until then it is worth saying out loud.
                self.stdout.write(self.style.WARNING(f"  {slug}: entitled but not installed"))
            else:
                self.stdout.write(self.style.WARNING(f"  {slug}: installed but not entitled — it must refuse to serve"))

        if not entitled and not installed:
            self.stdout.write("  (nothing)")
