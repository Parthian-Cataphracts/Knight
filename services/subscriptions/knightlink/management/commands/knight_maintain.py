"""
The service's own housekeeping, on a timer.

Two chores, and both of them are tables that grow for ever if nothing runs:

- **Used nonces.** Every signed request writes one, and they are what stops a
  captured request being replayed. They only need to outlive the skew window;
  keeping them for ever is a table that grows with traffic and is never read.
- **Secrets nobody can use any more.** A revoked or long-expired row is history,
  and history is worth keeping — but not for ever, and not the value. What is
  kept is the row with its dates and its value blanked, so "when did this key
  stop working" is still answerable and the key itself is gone.

Written as one command rather than two because it is one cron entry, and a
second entry is a second thing to forget. It is safe to run at any interval and
safe to run twice at once: both operations are deletes and updates over a
cut-off, and running them again finds nothing left to do.

    python manage.py knight_maintain
    python manage.py knight_maintain --loop --every 3600
"""

from __future__ import annotations

import logging
import time
from datetime import timedelta

from django.core.management.base import BaseCommand
from django.utils import timezone

from knightlink.models import StoreSecret
from knightlink.signing import forget_old_nonces

logger = logging.getLogger(__name__)

#: How long a secret nobody can use is kept before its value is blanked.
#:
#: Thirty days. Long enough that an incident weeks later can still see when a
#: key stopped working and what replaced it; short enough that a value which has
#: not verified a request in a month is not still sitting in a database waiting
#: to be stolen.
FORGET_SECRETS_AFTER = timedelta(days=30)

#: What replaces a forgotten secret. Nothing can sign with it — the row is long
#: expired or revoked before it is touched — and it is unique per row, which the
#: per-store uniqueness constraint requires.
FORGOTTEN = "forgotten:"


class Command(BaseCommand):
    help = "Forgets used nonces and blanks secrets nobody can use any more."

    def add_arguments(self, parser) -> None:
        parser.add_argument(
            "--loop",
            action="store_true",
            help="Keep running. For a container with no cron in it.",
        )
        parser.add_argument(
            "--every",
            type=int,
            default=3600,
            help="Seconds between passes when looping. Hourly by default.",
        )

    def handle(self, *args, **options) -> None:
        if not options["loop"]:
            self._sweep()
            return

        interval = max(60, int(options["every"]))
        self.stdout.write(f"Sweeping every {interval}s. Stop with SIGTERM.")

        while True:
            try:
                self._sweep()
            except Exception:  # noqa: BLE001
                # A sweep that failed must not end the loop. The tables it
                # tidies are the ones that hurt when nothing runs, and a
                # container that exited on a transient database error would stop
                # tidying them at exactly the moment something was already wrong.
                logger.exception("A maintenance sweep failed; the next one will try again.")

            time.sleep(interval)

    def _sweep(self) -> None:
        nonces = forget_old_nonces()
        secrets = self._forget_secrets()

        self.stdout.write(
            f"{timezone.now():%Y-%m-%d %H:%M:%S}  forgot {nonces} nonce(s), "
            f"blanked {secrets} spent secret(s)."
        )

    def _forget_secrets(self) -> int:
        """
        Throws away the value of every secret that has been unusable for a while.

        The row stays, and so do its dates: a rotation history is the answer to
        "when did this key stop working", which is the first question of any
        incident. What goes is the one part of it that is worth stealing.

        The value is replaced with a marker naming the row rather than with an
        empty string, because a store's secrets are unique per store and two
        blanked rows would collide — and a collision here would be a sweep that
        crashes on the second forgotten key rather than one that tidies up.
        """
        from django.db.models import Q

        cutoff = timezone.now() - FORGET_SECRETS_AFTER

        forgettable = (
            StoreSecret.objects.filter(Q(expires_at__lt=cutoff) | Q(revoked_at__lt=cutoff))
            .exclude(secret__startswith=FORGOTTEN)
            .only("id")
        )

        forgotten = 0

        for row in forgettable:
            StoreSecret.objects.filter(pk=row.pk).update(secret=f"{FORGOTTEN}{row.pk}")
            forgotten += 1

        return forgotten
