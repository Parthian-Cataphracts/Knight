"""
Deciding what is due, running it, and recording what happened.
"""

from __future__ import annotations

import json
import logging
import os
import tempfile
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from importlib import import_module
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)

#: What each schedule means. The store decides this, not KNIGHT: the manifest
#: says "daily" and the store is the only party that knows its own timezone and
#: when its quiet hours are.
INTERVALS = {
    "hourly": timedelta(hours=1),
    "daily": timedelta(days=1),
    "weekly": timedelta(days=7),
}

#: Beside the feature registry, and versioned for the same reason.
STATE_FILENAME = "worker-runs.json"
SCHEMA_VERSION = 1


@dataclass(frozen=True)
class WorkerOutcome:
    """What one run did."""

    slug: str
    name: str
    entrypoint: str
    succeeded: bool
    detail: str = ""
    skipped: bool = False

    def __str__(self) -> str:
        if self.skipped:
            return f"{self.slug}/{self.name}: not due"

        return f"{self.slug}/{self.name}: {'ok' if self.succeeded else 'FAILED'} {self.detail}".strip()


def _root(feature_root: str | Path | None) -> Path:
    if feature_root is not None:
        return Path(feature_root)

    from ..conf import get_settings

    return Path(get_settings().feature_root)


def _state_path(feature_root: str | Path | None) -> Path:
    return _root(feature_root) / STATE_FILENAME


def load_state(feature_root: str | Path | None = None) -> dict[str, Any]:
    """
    The last recorded run per worker.

    A missing file is an empty history, which is correct for a store that has
    never run one. A *corrupt* file is refused rather than read as empty:
    treating it as empty would run every worker at once, and for a worker that
    sends email that is a store mailing its whole customer list twice.
    """
    path = _state_path(feature_root)

    if not path.exists():
        return {"schemaVersion": SCHEMA_VERSION, "runs": {}}

    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise RuntimeError(
            f"The worker run history at {path} could not be read: {error}. "
            "Refusing to treat it as empty - that would run every worker at once."
        ) from error

    document.setdefault("runs", {})

    return document


def save_state(document: dict[str, Any], feature_root: str | Path | None = None) -> None:
    """
    Writes the history atomically.

    Same reasoning as the feature registry: a half-written file is worse than no
    file, because the next run reads it.
    """
    path = _state_path(feature_root)
    path.parent.mkdir(parents=True, exist_ok=True)

    handle, temporary = tempfile.mkstemp(dir=str(path.parent), prefix=".worker-runs-", suffix=".json")

    try:
        with os.fdopen(handle, "w", encoding="utf-8") as file:
            json.dump(document, file, indent=2, sort_keys=True)
            file.write("\n")

        os.replace(temporary, path)
    except BaseException:
        Path(temporary).unlink(missing_ok=True)
        raise


def _key(slug: str, name: str) -> str:
    return f"{slug}:{name}"


def _is_due(last_run: str | None, schedule: str, now: datetime) -> bool:
    """
    Whether a worker should run.

    A worker that has never run is due immediately. An unknown schedule is
    treated as daily and logged: refusing to run it at all would be a Feature
    that silently does nothing, and the manifest validation at publish already
    rejects anything KNIGHT does not know — so an unknown value here means a
    store is running a package from somewhere else.
    """
    if last_run is None:
        return True

    interval = INTERVALS.get(schedule)

    if interval is None:
        logger.warning("Unknown worker schedule '%s'; treating it as daily.", schedule)
        interval = INTERVALS["daily"]

    try:
        previous = datetime.fromisoformat(last_run)
    except ValueError:
        # An unreadable timestamp is treated as never-run rather than as now.
        # The cost of one extra run is a duplicate job; the cost of the other
        # mistake is a worker that never runs again.
        return True

    if previous.tzinfo is None:
        previous = previous.replace(tzinfo=timezone.utc)

    return now - previous >= interval


def due_workers(
    *, feature_root: str | Path | None = None, at: datetime | None = None, force: bool = False
) -> list[tuple[str, dict[str, Any]]]:
    """
    Every worker that should run now, as (slug, worker) pairs.

    Only enabled Features are considered. A Feature whose entitlement lapsed is
    still on disk with its data intact and must not serve — and a scheduled job
    is serving.
    """
    from ..installer.state import get_registry

    now = at or datetime.now(timezone.utc)
    state = load_state(feature_root)
    runs = state.get("runs", {})

    due: list[tuple[str, dict[str, Any]]] = []

    for feature in get_registry(_root(feature_root)).enabled_features():
        for worker in feature.workers or []:
            name = worker.get("name")
            entrypoint = worker.get("entrypoint")

            if not name or not entrypoint:
                continue

            last = (runs.get(_key(feature.slug, name)) or {}).get("finishedAt")

            if force or _is_due(last, str(worker.get("schedule", "daily")).lower(), now):
                due.append((feature.slug, worker))

    return due


def _call(entrypoint: str) -> Any:
    """
    Imports and calls one entrypoint.

    Split on the last dot, so `pkg.module.function` works and so does
    `pkg.function`. The callable takes no arguments: a worker that took
    parameters would need somewhere for them to come from, and the only honest
    source is the Feature's own configuration, which it can read itself.
    """
    module_path, _, attribute = entrypoint.rpartition(".")

    if not module_path or not attribute:
        raise ValueError(f"'{entrypoint}' is not a module path and a callable name.")

    target = getattr(import_module(module_path), attribute)

    if not callable(target):
        raise TypeError(f"'{entrypoint}' is not callable.")

    return target()


def run_due(
    *,
    feature_root: str | Path | None = None,
    at: datetime | None = None,
    force: bool = False,
    dry_run: bool = False,
) -> list[WorkerOutcome]:
    """
    Runs everything that is due and records what happened.

    Each worker is isolated: one that raises loses its own run and nothing else.
    A failure is recorded with its reason and the *last successful* run is left
    alone, so a job that has been failing for three days is still due rather
    than quietly rescheduled.
    """
    now = at or datetime.now(timezone.utc)
    outcomes: list[WorkerOutcome] = []
    state = load_state(feature_root)
    runs = state.setdefault("runs", {})

    for slug, worker in due_workers(feature_root=feature_root, at=now, force=force):
        name = str(worker["name"])
        entrypoint = str(worker["entrypoint"])

        if dry_run:
            outcomes.append(
                WorkerOutcome(slug, name, entrypoint, succeeded=True, detail="would run", skipped=True)
            )
            continue

        record = runs.setdefault(_key(slug, name), {})
        record["entrypoint"] = entrypoint
        record["startedAt"] = now.isoformat()

        try:
            result = _call(entrypoint)
        except Exception as error:  # noqa: BLE001 - one bad worker must not stop the rest
            logger.exception("Worker '%s' of feature '%s' failed.", name, slug)

            record["failedAt"] = now.isoformat()
            record["error"] = str(error)[:500]
            # `finishedAt` is deliberately not moved: a job that has been failing
            # for three days is still due, not rescheduled.
            outcomes.append(WorkerOutcome(slug, name, entrypoint, succeeded=False, detail=str(error)[:200]))
            continue

        record["finishedAt"] = now.isoformat()
        record.pop("error", None)
        record.pop("failedAt", None)
        record["result"] = str(result)[:200] if result is not None else ""

        outcomes.append(
            WorkerOutcome(slug, name, entrypoint, succeeded=True, detail=record["result"])
        )

    if not dry_run:
        save_state(state, feature_root)

    return outcomes
