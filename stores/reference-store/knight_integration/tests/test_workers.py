"""
The worker runner.

A Feature declares a scheduled job in its manifest, KNIGHT delivers the
declaration with the install, and this is what makes it true on the store. What
matters here is the failure behaviour, because a worker runs on a timer with
nobody watching: one bad job must not stop the others, every run must be
recorded including the failures, and running twice must be safe.
"""

from __future__ import annotations

import json
import shutil
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

from django.test import SimpleTestCase

from knight_integration.installer.state import InstalledFeature, get_registry
from knight_integration.workers import runner

# Module-level flags the fake entrypoints below record themselves in. A worker
# takes no arguments and returns a value, so this is how a test observes one.
CALLS: list[str] = []


def works() -> int:
    CALLS.append("works")
    return 7


def also_works() -> str:
    CALLS.append("also_works")
    return "done"


def explodes():
    CALLS.append("explodes")
    raise RuntimeError("the worker itself broke")


NOT_CALLABLE = "this is a string, not a function"


class WorkerRunnerTests(SimpleTestCase):
    def setUp(self) -> None:
        CALLS.clear()
        self.root = Path(tempfile.mkdtemp(prefix="knight-workers-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        self.now = datetime(2026, 8, 26, 12, 0, tzinfo=timezone.utc)

    def _install(self, slug: str, workers: list[dict], *, enabled: bool = True) -> None:
        get_registry(self.root).record(
            InstalledFeature(
                slug=slug,
                version="1.0.0",
                app_label=slug.replace("-", "_"),
                installed_app=slug.replace("-", "_"),
                digest="sha256:test",
                installed_at=self.now.isoformat(),
                enabled=enabled,
                workers=workers,
            )
        )

    @staticmethod
    def _worker(name: str, function: str, schedule: str = "daily") -> dict:
        return {
            "name": name,
            "entrypoint": f"knight_integration.tests.test_workers.{function}",
            "schedule": schedule,
        }

    def _run(self, **kwargs):
        return runner.run_due(feature_root=self.root, at=self.now, **kwargs)

    # --- the ordinary path -------------------------------------------------

    def test_a_worker_that_has_never_run_is_due(self):
        self._install("alpha", [self._worker("nightly", "works")])

        outcomes = self._run()

        self.assertEqual(CALLS, ["works"])
        self.assertTrue(outcomes[0].succeeded)

    def test_its_return_value_is_recorded_so_an_operator_can_read_it(self):
        # "Did the nightly job run" is the first question anybody asks, and
        # "it expired 99 points" is the answer they want.
        self._install("alpha", [self._worker("nightly", "works")])

        self.assertEqual(self._run()[0].detail, "7")

    def test_nothing_is_due_immediately_afterwards(self):
        self._install("alpha", [self._worker("nightly", "works")])
        self._run()
        CALLS.clear()

        self.assertEqual(self._run(), [])
        self.assertEqual(CALLS, [])

    def test_it_is_due_again_once_the_interval_has_passed(self):
        self._install("alpha", [self._worker("nightly", "works")])
        self._run()
        CALLS.clear()

        outcomes = runner.run_due(feature_root=self.root, at=self.now + timedelta(days=1, minutes=1))

        self.assertEqual(CALLS, ["works"])
        self.assertTrue(outcomes[0].succeeded)

    def test_an_hourly_worker_is_not_due_after_a_minute(self):
        self._install("alpha", [self._worker("often", "works", schedule="hourly")])
        self._run()
        CALLS.clear()

        runner.run_due(feature_root=self.root, at=self.now + timedelta(minutes=1))

        self.assertEqual(CALLS, [])

    def test_force_runs_it_whether_or_not_it_is_due(self):
        # For an operator who needs the job now.
        self._install("alpha", [self._worker("nightly", "works")])
        self._run()
        CALLS.clear()

        self._run(force=True)

        self.assertEqual(CALLS, ["works"])

    def test_a_dry_run_reports_without_running(self):
        self._install("alpha", [self._worker("nightly", "works")])

        outcomes = self._run(dry_run=True)

        self.assertEqual(CALLS, [])
        self.assertTrue(outcomes[0].skipped)
        # And it must not have recorded a run, or the real one would be skipped.
        self.assertEqual(runner.load_state(self.root)["runs"], {})

    # --- failure behaviour -------------------------------------------------

    def test_one_failing_worker_does_not_stop_the_others(self):
        # A Feature with a raising job loses its own run and nothing else. The
        # store keeps selling.
        self._install("alpha", [self._worker("bad", "explodes")])
        self._install("beta", [self._worker("good", "also_works")])

        outcomes = self._run()

        self.assertIn("also_works", CALLS)
        self.assertEqual({o.succeeded for o in outcomes}, {True, False})

    def test_a_failure_is_recorded_with_its_reason(self):
        self._install("alpha", [self._worker("bad", "explodes")])

        outcomes = self._run()

        self.assertFalse(outcomes[0].succeeded)
        self.assertIn("the worker itself broke", outcomes[0].detail)

        record = runner.load_state(self.root)["runs"]["alpha:bad"]
        self.assertIn("the worker itself broke", record["error"])

    def test_a_failing_worker_stays_due_rather_than_being_rescheduled(self):
        # A job that has been failing for three days is still due. Moving its
        # next-run forward on failure is how a broken job goes quiet.
        self._install("alpha", [self._worker("bad", "explodes")])
        self._run()
        CALLS.clear()

        self._run()

        self.assertEqual(CALLS, ["explodes"])

    def test_a_worker_that_recovers_clears_its_recorded_error(self):
        self._install("alpha", [self._worker("flaky", "explodes")])
        self._run()

        self._install("alpha", [self._worker("flaky", "works")])
        self._run()

        record = runner.load_state(self.root)["runs"]["alpha:flaky"]
        self.assertNotIn("error", record)
        self.assertIn("finishedAt", record)

    def test_an_entrypoint_that_does_not_exist_is_a_failure_not_a_crash(self):
        self._install("alpha", [self._worker("missing", "no_such_function")])

        outcomes = self._run()

        self.assertFalse(outcomes[0].succeeded)

    def test_an_entrypoint_that_is_not_callable_is_refused(self):
        self._install("alpha", [self._worker("wrong", "NOT_CALLABLE")])

        outcomes = self._run()

        self.assertFalse(outcomes[0].succeeded)
        self.assertIn("not callable", outcomes[0].detail)

    def test_a_malformed_entrypoint_is_refused(self):
        self._install("alpha", [{"name": "bare", "entrypoint": "nodots", "schedule": "daily"}])

        outcomes = self._run()

        self.assertFalse(outcomes[0].succeeded)

    # --- what must not run -------------------------------------------------

    def test_a_disabled_feature_does_not_run_its_workers(self):
        # An entitlement that lapsed leaves the code and its data in place and
        # must not serve — and a scheduled job is serving.
        self._install("alpha", [self._worker("nightly", "works")], enabled=False)

        self.assertEqual(self._run(), [])
        self.assertEqual(CALLS, [])

    def test_a_feature_with_no_workers_contributes_nothing(self):
        self._install("alpha", [])

        self.assertEqual(self._run(), [])

    def test_a_worker_missing_its_entrypoint_is_ignored(self):
        self._install("alpha", [{"name": "half", "schedule": "daily"}])

        self.assertEqual(self._run(), [])

    # --- the run history ---------------------------------------------------

    def test_the_history_is_readable_during_an_incident(self):
        self._install("alpha", [self._worker("nightly", "works")])
        self._run()

        document = json.loads((self.root / runner.STATE_FILENAME).read_text(encoding="utf-8"))

        self.assertEqual(document["schemaVersion"], runner.SCHEMA_VERSION)
        self.assertIn("alpha:nightly", document["runs"])

    def test_a_corrupt_history_is_refused_rather_than_read_as_empty(self):
        # Treating it as empty would run every worker at once, and for a worker
        # that sends email that is a store mailing its whole list twice.
        self._install("alpha", [self._worker("nightly", "works")])
        (self.root / runner.STATE_FILENAME).write_text("{ not json", encoding="utf-8")

        with self.assertRaises(RuntimeError):
            self._run()

        self.assertEqual(CALLS, [])

    def test_a_write_leaves_no_temporary_files_behind(self):
        self._install("alpha", [self._worker("nightly", "works")])
        self._run()

        leftovers = [path.name for path in self.root.glob(".worker-runs-*")]

        self.assertEqual(leftovers, [])

    def test_an_unreadable_timestamp_is_treated_as_never_run(self):
        # One extra run costs a duplicate job. The other mistake is a worker
        # that never runs again.
        self._install("alpha", [self._worker("nightly", "works")])
        runner.save_state(
            {"schemaVersion": 1, "runs": {"alpha:nightly": {"finishedAt": "not a date"}}}, self.root
        )

        self._run()

        self.assertEqual(CALLS, ["works"])

    def test_an_unknown_schedule_falls_back_to_daily_rather_than_never(self):
        # Manifest validation rejects unknown schedules at publish, so a value
        # here means a store is running a package from somewhere else. Refusing
        # to run it at all would be a Feature that silently does nothing.
        self._install("alpha", [self._worker("odd", "works", schedule="fortnightly")])

        self._run()

        self.assertEqual(CALLS, ["works"])
