"""
Runs any installation job KNIGHT has queued for this store.

This is what a scheduler invokes — a cron entry, a systemd timer, or the agent
proper in phase 4. It is a management command rather than a daemon because a
command is trivially observable: it exits with a status, writes to a log, and
can be run by hand during an incident to see exactly what happens.
"""

from __future__ import annotations

from django.core.management.base import BaseCommand, CommandError

from knight_integration.conf import KnightConfigurationError, get_settings
from knight_integration.installer import JobRunner


class Command(BaseCommand):
    help = "Claims and runs the next feature installation job queued for this store."

    def add_arguments(self, parser) -> None:
        parser.add_argument(
            "--max-jobs",
            type=int,
            default=1,
            help="How many queued jobs to run before exiting. KNIGHT hands out one at a time.",
        )

    def handle(self, *args, **options) -> None:
        config = get_settings()

        try:
            config.require_credentials()
        except KnightConfigurationError as exc:
            raise CommandError(str(exc)) from exc

        runner = JobRunner(config=config)
        ran = 0

        for _ in range(max(1, options["max_jobs"])):
            outcome = runner.run_once()

            if outcome is None:
                break

            ran += 1

            if outcome.succeeded:
                self.stdout.write(self.style.SUCCESS(f"Job succeeded ({outcome.installed_version or 'no version'})."))
            else:
                # Not a CommandError: the job failed, the command did what it was
                # asked, and KNIGHT has already been told. Exiting non-zero here
                # would make a scheduler treat a correctly-reported feature
                # failure as a broken agent.
                self.stdout.write(
                    self.style.ERROR(
                        f"Job failed: {outcome.failure_code} — {outcome.failure_message} "
                        f"(rollback: {outcome.rollback_outcome})"
                    )
                )

        if ran == 0:
            self.stdout.write("No installation jobs are queued for this store.")
