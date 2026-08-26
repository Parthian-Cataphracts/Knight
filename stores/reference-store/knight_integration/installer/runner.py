"""
Running one installation job.

The runner is the store half of the pipeline in docs/feature-delivery.md §7. It
claims a job, walks its steps, reports each one, and reports the outcome. It is
the only thing in this package that decides to change the store.

Three properties it exists to guarantee:

- **It only ever does what the job names.** The step vocabulary is fixed here,
  and a job type or step KNIGHT invents that this agent does not know is refused
  rather than improvised. An agent that executed instructions it did not
  recognise would be a remote shell with extra steps
  (docs/feature-delivery.md §15).
- **It resumes rather than restarts.** KNIGHT says which step comes next; the
  runner starts there. Re-running an install from the top after a failure at
  step eight would re-apply seven steps that already succeeded.
- **It is honest about rollback.** What it could undo, it undoes. What it cannot
  — an irreversible migration that has already applied — it stops at and says
  so, so KNIGHT can raise an incident instead of a store quietly sitting in a
  state nobody chose.
"""

from __future__ import annotations

import logging
import shutil
import time
from collections.abc import Callable
from pathlib import Path
from typing import Any

from ..client import KnightClient, KnightRejected, KnightUnavailable
from ..conf import get_settings
from . import steps
from .state import get_registry
from .steps import JobContext, StepFailed

logger = logging.getLogger(__name__)

#: The step implementations, by the name KNIGHT uses. A job asking for anything
#: not in this table is refused: this table *is* the agent's whole vocabulary.
STEP_IMPLEMENTATIONS: dict[str, Callable[[JobContext], str]] = {
    "preflight": steps.preflight,
    "fetch": steps.fetch,
    "verify": steps.verify,
    "backup": steps.backup,
    "install": steps.install,
    "create-extensions": steps.create_extensions,
    "migrate": steps.migrate,
    "configure": steps.configure,
    "enable": steps.enable,
    "reload": steps.reload,
    "healthcheck": steps.healthcheck,
    "disable": steps.disable,
    "remove-package": steps.remove_package,
    "restore-package": steps.restore_package,
    "reverse-migrate": steps.reverse_migrate,
}

#: The job types this agent understands. Anything else is refused and reported,
#: never attempted.
KNOWN_JOB_TYPES = frozenset(
    {"Install", "Upgrade", "ApplyConfiguration", "Enable", "Disable", "Uninstall", "Rollback"}
)

#: Steps whose rollback is the same operation in reverse. Anything not here has
#: nothing to undo — a fetch leaves only a temporary file, a health check leaves
#: nothing at all.
#:
#: `create-extensions` is absent deliberately rather than by omission. It does
#: change the database, and it is still not undone: an extension is shared with
#: the store and with every other feature installed in the same database, so a
#: rollback dropping one could break a feature it has never heard of
#: (docs/adr/0031-database-extensions-are-declared-not-migrated.md).
_ROLLBACK_FOR = {
    "install": "restore-package",
    "migrate": "reverse-migrate",
    "configure": "configure",
}


class JobOutcome:
    """What happened, in the shape KNIGHT's completion report expects."""

    def __init__(
        self,
        succeeded: bool,
        failure_code: str | None = None,
        failure_message: str | None = None,
        rollback_outcome: str = "NotAttempted",
        installed_version: str | None = None,
        health: str | None = None,
    ) -> None:
        self.succeeded = succeeded
        self.failure_code = failure_code
        self.failure_message = failure_message
        self.rollback_outcome = rollback_outcome
        self.installed_version = installed_version
        self.health = health

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return (
            f"JobOutcome(succeeded={self.succeeded}, failure_code={self.failure_code!r}, "
            f"rollback_outcome={self.rollback_outcome!r})"
        )


class JobRunner:
    """Claims and executes installation jobs."""

    def __init__(self, client: KnightClient | None = None, config=None) -> None:
        self._config = config or get_settings()
        self._client = client or KnightClient(self._config)

    def run_once(self) -> JobOutcome | None:
        """
        Claims one job and runs it, or returns None when there is nothing to do.

        Nothing to do is the normal case and is not logged at anything above
        debug: an agent polling every thirty seconds would otherwise fill a log
        with the news that it had no work.
        """
        try:
            job = self._client.claim_job()
        except (KnightUnavailable, KnightRejected) as exc:
            logger.warning("Could not claim a job from KNIGHT: %s", exc)
            return None

        if not job:
            logger.debug("No installation job is queued for this store.")
            return None

        return self.execute(job)

    def execute(self, job: dict[str, Any]) -> JobOutcome:
        """Runs one job to completion and reports the outcome."""
        job_id = str(job.get("jobId"))
        job_type = str(job.get("type"))
        slug = job.get("featureSlug", "")

        if job_type not in KNOWN_JOB_TYPES:
            # Refused rather than attempted. This is the check that keeps a
            # compromised or simply newer control plane from talking this agent
            # into doing something it was never built to do.
            outcome = JobOutcome(
                False,
                "job.unknown_type",
                f"This agent does not understand job type '{job_type}' and will not attempt it.",
            )
            self._report_completion(job_id, outcome)
            return outcome

        workspace = steps.make_workspace(slug or "job")
        context = JobContext(
            job=job,
            config=self._config,
            registry=get_registry(self._config.feature_root),
            workspace=workspace,
        )

        try:
            return self._run_pipeline(context, job_id, job_type)
        finally:
            shutil.rmtree(workspace, ignore_errors=True)

    def _run_pipeline(self, context: JobContext, job_id: str, job_type: str) -> JobOutcome:
        pipeline: list[str] = list(context.job.get("steps") or [])
        next_step = context.job.get("nextStep")

        # Resume where KNIGHT says, not from the top: the steps before it have
        # already been applied and reported.
        if next_step in pipeline:
            pipeline = pipeline[pipeline.index(next_step):]

        for name in pipeline:
            implementation = STEP_IMPLEMENTATIONS.get(name)

            if implementation is None:
                outcome = JobOutcome(
                    False,
                    "job.unknown_step",
                    f"This agent does not implement the step '{name}' and will not guess at it.",
                )
                self._report_completion(job_id, outcome)
                return outcome

            # An Enable job's "enable" step re-enables what is installed; an
            # Install job's records a brand new feature. Same name, different
            # meaning, so the job type decides which runs.
            if name == "enable" and job_type == "Enable":
                implementation = steps.enable_existing

            started = time.monotonic()
            self._report_step(job_id, name, "Running")

            try:
                output = implementation(context)
            except StepFailed as exc:
                duration = int((time.monotonic() - started) * 1000)
                self._report_step(job_id, name, "Failed", exc.detail, exc.code, duration)
                logger.error("Step %s of job %s failed: %s", name, job_id, exc.detail)

                return self._roll_back_and_report(context, job_id, exc)
            except Exception as exc:  # noqa: BLE001 - an unexpected failure is still a failed step
                duration = int((time.monotonic() - started) * 1000)
                detail = f"{type(exc).__name__}: {exc}"
                self._report_step(job_id, name, "Failed", detail, "step.unexpected", duration)
                logger.exception("Step %s of job %s raised unexpectedly.", name, job_id)

                return self._roll_back_and_report(context, job_id, StepFailed("step.unexpected", detail))

            duration = int((time.monotonic() - started) * 1000)

            # A step that decided there was nothing to do reports Skipped, not
            # Succeeded. The job record should say which it was.
            status = "Skipped" if _looks_skipped(output) else "Succeeded"
            self._report_step(job_id, name, status, output, None, duration)

        installed = context.registry.get(context.slug)
        outcome = JobOutcome(
            True,
            installed_version=installed.version if installed else None,
            health="Healthy",
        )

        self._report_completion(job_id, outcome)
        return outcome

    def _roll_back_and_report(self, context: JobContext, job_id: str, failure: StepFailed) -> JobOutcome:
        """
        Undoes what this job applied, in reverse, and reports how far it got.

        The three outcomes are genuinely different and are never collapsed:
        nothing needed undoing, everything was undone, or a boundary was reached
        that only a person can cross.
        """
        if not context.applied:
            outcome = JobOutcome(False, failure.code, failure.detail, "NotAttempted")
            self._report_completion(job_id, outcome)
            return outcome

        rollback_outcome = "RolledBack"

        for applied in reversed(context.applied):
            undo_name = _ROLLBACK_FOR.get(applied)
            if undo_name is None:
                continue

            implementation = STEP_IMPLEMENTATIONS[undo_name]

            try:
                output = implementation(context)
                self._report_step(job_id, undo_name, "Succeeded", output)
            except StepFailed as exc:
                self._report_step(job_id, undo_name, "Failed", exc.detail, exc.code)

                # An irreversible migration is the one failure that stops the
                # rollback dead. Continuing past it would leave the package and
                # the schema disagreeing about which version this store runs.
                if exc.code == "rollback.irreversible":
                    rollback_outcome = "ManualInterventionRequired"
                else:
                    rollback_outcome = "PartiallyRolledBack"

                break
            except Exception:  # noqa: BLE001
                logger.exception("Rollback step %s failed unexpectedly.", undo_name)
                rollback_outcome = "PartiallyRolledBack"
                break

        outcome = JobOutcome(False, failure.code, failure.detail, rollback_outcome)
        self._report_completion(job_id, outcome)
        return outcome

    def _report_step(
        self,
        job_id: str,
        step: str,
        status: str,
        output: str | None = None,
        error_code: str | None = None,
        duration_ms: int | None = None,
    ) -> None:
        """
        Tells KNIGHT how a step went.

        A failure to report is logged and swallowed. The work has already
        happened on this store, and abandoning a half-finished install because a
        progress update did not send would turn a network blip into an outage.
        KNIGHT's claim timeout is what catches an agent that has genuinely
        stopped talking.
        """
        try:
            self._client.report_step(job_id, step, status, output, error_code, duration_ms)
        except (KnightUnavailable, KnightRejected) as exc:
            logger.warning("Could not report step %s of job %s to KNIGHT: %s", step, job_id, exc)

    def _report_completion(self, job_id: str, outcome: JobOutcome) -> None:
        try:
            self._client.complete_job(
                job_id,
                outcome.succeeded,
                outcome.failure_code,
                outcome.failure_message,
                outcome.rollback_outcome,
                outcome.installed_version,
                outcome.health,
            )
        except (KnightUnavailable, KnightRejected) as exc:
            # The store is in whatever state the job left it. KNIGHT will time
            # the claim out and reconciliation will find the disagreement, which
            # is precisely why the store reports what is on disk rather than what
            # anyone expected to be there.
            logger.error("Could not report the outcome of job %s to KNIGHT: %s", job_id, exc)


def _looks_skipped(output: str) -> bool:
    """
    Whether a step's own words say it did nothing.

    A small piece of string matching, and worth it: the alternative is every step
    returning a status enum alongside its message, which makes fourteen simple
    functions more ceremonious to gain the same information.
    """
    lowered = (output or "").lower()
    return lowered.startswith(("no ", "nothing", "the manifest declares no", "the feature declares no"))
