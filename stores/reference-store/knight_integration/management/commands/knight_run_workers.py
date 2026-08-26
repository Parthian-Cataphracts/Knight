"""
`manage.py knight_run_workers` — run the scheduled jobs installed Features declare.

Put this on the store's cron, as often as the shortest schedule any Feature
uses. Every fifteen minutes is a reasonable default: the command decides what is
actually due, so firing it more often than anything needs costs a file read.

    */15 * * * * cd /srv/store && .venv/bin/python manage.py knight_run_workers

Safe to run twice, and safe to run by hand after an outage. What is due is
computed from the last *successful* run of each worker, so a job that has been
failing is still due rather than quietly rescheduled.
"""

from __future__ import annotations

from django.core.management.base import BaseCommand

from ...workers import run_due


class Command(BaseCommand):
    help = "Runs the scheduled jobs declared by installed Features, when they are due."

    def add_arguments(self, parser):
        parser.add_argument(
            "--dry-run",
            action="store_true",
            help="List what is due without running any of it.",
        )
        parser.add_argument(
            "--force",
            action="store_true",
            help="Run every worker whether or not it is due. For an operator who needs one now.",
        )

    def handle(self, *args, **options):
        outcomes = run_due(force=options["force"], dry_run=options["dry_run"])

        if not outcomes:
            self.stdout.write("Nothing is due.")
            return

        failed = 0

        for outcome in outcomes:
            if outcome.skipped:
                self.stdout.write(f"  would run  {outcome.slug}/{outcome.name}")
            elif outcome.succeeded:
                self.stdout.write(self.style.SUCCESS(f"  ran        {outcome.slug}/{outcome.name}  {outcome.detail}"))
            else:
                failed += 1
                self.stdout.write(self.style.ERROR(f"  FAILED     {outcome.slug}/{outcome.name}  {outcome.detail}"))

        if options["dry_run"]:
            self.stdout.write(self.style.WARNING(f"{len(outcomes)} worker(s) due. Dry run - nothing ran."))
            return

        ran = len(outcomes) - failed
        self.stdout.write(f"{ran} ran, {failed} failed.")

        if failed:
            # A non-zero exit so that whatever runs this on a timer can alert on
            # it. A cron entry whose failures are invisible is a cron entry
            # nobody finds out about until a customer does.
            raise SystemExit(1)
