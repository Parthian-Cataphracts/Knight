"""
The individual steps of an installation job.

Each step is a plain function taking the job context and returning a short line
of output. They are separate functions rather than one procedure because KNIGHT
records them separately, retries resume at the first unfinished one, and a
rollback walks the succeeded ones backwards — none of which is possible if
"install the feature" is a single opaque call.

Every step is idempotent. A step that has already been applied notices and says
so instead of applying again, because an agent that lost a reply will re-run it
(docs/feature-delivery.md §7).
"""

from __future__ import annotations

import logging
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import requests

from ..conf import KnightSettings
from .state import InstallationRegistry, InstalledFeature
from .verify import ArtifactRejected, verify_artifact

logger = logging.getLogger(__name__)


class StepFailed(RuntimeError):
    """A step could not complete. Carries the code KNIGHT records and alerts on."""

    def __init__(self, code: str, detail: str) -> None:
        super().__init__(detail)
        self.code = code
        self.detail = detail


@dataclass
class JobContext:
    """Everything a step needs, assembled once when the job is claimed."""

    job: dict[str, Any]
    config: KnightSettings
    registry: InstallationRegistry
    workspace: Path

    #: Where the verified artifact landed, once the fetch step has run.
    artifact_path: Path | None = None

    #: What the feature looked like before this job started, or None on a first
    #: install. This is the rollback target, captured before anything changes.
    previous: InstalledFeature | None = None

    #: Steps that have actually changed something, newest last. The rollback
    #: walks this rather than the pipeline, so it never tries to undo a step that
    #: was skipped.
    applied: list[str] = field(default_factory=list)

    @property
    def slug(self) -> str:
        return self.job.get("featureSlug", "")

    @property
    def version(self) -> str:
        return self.job.get("targetVersion") or ""

    @property
    def artifact(self) -> dict[str, Any]:
        return self.job.get("artifact") or {}

    @property
    def migrations(self) -> dict[str, Any]:
        return self.job.get("migrations") or {}

    @property
    def target_dir(self) -> Path:
        """Where this feature's package lives. One directory per feature, per version."""
        return Path(self.config.feature_root) / self.slug

    def manifest_value(self, key: str, default: Any = None) -> Any:
        return (self.job.get("configuration") or {}).get(key, default)


# --- Steps ------------------------------------------------------------------


def preflight(context: JobContext) -> str:
    """
    Checks the things that make the rest of the job impossible, before it starts.

    Cheap and boring on purpose. Discovering there is no disk space after
    migrating a database is how a store ends up in a state nobody planned for.
    """
    artifact = context.artifact

    if context.job.get("type") not in {"Disable", "Enable", "Uninstall", "ApplyConfiguration"} and not artifact:
        raise StepFailed("preflight.no_artifact", "The job names no artifact to install.")

    size = int(artifact.get("sizeBytes") or 0)
    if size > context.config.max_artifact_bytes:
        raise StepFailed(
            "preflight.too_large",
            f"The artifact is {size} bytes, above this store's limit of {context.config.max_artifact_bytes}.",
        )

    root = Path(context.config.feature_root)
    try:
        root.mkdir(parents=True, exist_ok=True)
    except OSError as exc:
        raise StepFailed("preflight.unwritable", f"The feature root {root} is not writable: {exc}") from exc

    if size:
        free = shutil.disk_usage(root).free
        # Three times the artifact: the download, the unpacked tree, and room to
        # keep the previous version until the new one is known to work.
        if free < size * 3:
            raise StepFailed(
                "preflight.disk_full",
                f"{free} bytes free at {root}; installing {context.slug} needs roughly {size * 3}.",
            )

    context.previous = context.registry.get(context.slug)
    return f"python {sys.version_info.major}.{sys.version_info.minor}, {root} writable"


def fetch(context: JobContext) -> str:
    """
    Downloads the artifact to the workspace.

    Streamed to disk rather than held in memory, and capped: a URL that keeps
    sending must not be able to fill the disk just because the declared size said
    otherwise.
    """
    artifact = context.artifact
    url = artifact.get("downloadUrl")

    if not url:
        raise StepFailed("fetch.no_url", "The job carries no download URL.")

    destination = context.workspace / f"{context.slug}-{context.version}.artifact"
    limit = min(int(artifact.get("sizeBytes") or 0) or context.config.max_artifact_bytes, context.config.max_artifact_bytes)
    written = 0

    try:
        with requests.get(url, stream=True, timeout=context.config.timeout_seconds * 6) as response:
            response.raise_for_status()

            with destination.open("wb") as handle:
                for chunk in response.iter_content(chunk_size=1024 * 256):
                    written += len(chunk)

                    if written > limit:
                        raise StepFailed(
                            "fetch.oversized",
                            f"The download exceeded the declared size of {limit} bytes.",
                        )

                    handle.write(chunk)
    except requests.RequestException as exc:
        raise StepFailed("fetch.failed", f"The artifact could not be downloaded: {exc}") from exc

    context.artifact_path = destination
    return f"{written} bytes"


def verify(context: JobContext) -> str:
    """
    Refuses to go further unless the bytes are what KNIGHT published and a key
    this store trusts signed them.

    Nothing has been changed on the store yet at this point, which is exactly
    where this check belongs.
    """
    if context.artifact_path is None or not context.artifact_path.exists():
        raise StepFailed("verify.no_artifact", "There is nothing to verify; the fetch step did not run.")

    artifact = context.artifact

    try:
        digest = verify_artifact(
            context.artifact_path,
            artifact.get("digest", ""),
            artifact.get("signature", ""),
            artifact.get("signingKeyId", ""),
            context.config.signing_keys,
        )
    except ArtifactRejected as exc:
        # Reported with the rejection's own code so KNIGHT can tell a corrupt
        # download from a signature that did not check out. They mean very
        # different things.
        raise StepFailed(exc.code, exc.detail) from exc

    return f"digest {digest[:12]} signed by {artifact.get('signingKeyId')}"


def backup(context: JobContext) -> str:
    """
    Records what to return to, and keeps the current package tree.

    The tree is moved aside rather than deleted so that restoring it is a rename
    rather than a second download — which matters when the reason for the
    rollback is that the network is broken.
    """
    if context.previous is None or not context.target_dir.exists():
        return "nothing installed yet; no backup needed"

    backup_dir = context.workspace / "previous"
    if backup_dir.exists():
        shutil.rmtree(backup_dir, ignore_errors=True)

    shutil.copytree(context.target_dir, backup_dir)
    context.applied.append("backup")

    return f"kept {context.previous.slug} {context.previous.version}"


def install(context: JobContext) -> str:
    """
    Unpacks the verified artifact into the feature's directory.

    Unpacked into a temporary directory first and then swapped into place, so a
    partly-extracted archive is never what the store tries to import. Members are
    checked for paths that escape the destination: an archive is untrusted input
    even after its signature checks out, because a signature says who built it,
    not that they built it carefully.
    """
    if context.artifact_path is None:
        raise StepFailed("install.no_artifact", "There is nothing to install.")

    staging = context.workspace / "staging"
    if staging.exists():
        shutil.rmtree(staging, ignore_errors=True)
    staging.mkdir(parents=True)

    path = context.artifact_path

    try:
        if zipfile.is_zipfile(path):
            _extract_zip(path, staging)
        elif tarfile.is_tarfile(path):
            _extract_tar(path, staging)
        else:
            raise StepFailed("install.unknown_format", "The artifact is neither a zip nor a tar archive.")
    except StepFailed:
        raise
    except (OSError, zipfile.BadZipFile, tarfile.TarError) as exc:
        raise StepFailed("install.unpack_failed", f"The artifact could not be unpacked: {exc}") from exc

    target = context.target_dir
    if target.exists():
        shutil.rmtree(target)

    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.move(str(staging), str(target))
    context.applied.append("install")

    return f"unpacked into {target}"


#: Database extensions this store is willing to create because a job asked it to.
#:
#: The same closed list KNIGHT validates a manifest against at publish, repeated
#: here on purpose. The store does not trust the job body — it re-checks a
#: signature it already asked for, and it normalises workers rather than running
#: what it is sent — and an extension is the strongest case for that rule: some
#: PostgreSQL extensions are procedural languages, and creating one is arbitrary
#: code execution against the database owner. The publish-time check protects
#: every store from a careless author; this one protects *this* store from a
#: KNIGHT that has been compromised or has simply moved on
#: (docs/adr/0031-database-extensions-are-declared-not-migrated.md).
ALLOWED_EXTENSIONS = frozenset(
    {"pg_trgm", "btree_gin", "btree_gist", "unaccent", "citext", "pgcrypto"}
)


def create_extensions(context: JobContext) -> str:
    """
    Creates the database extensions the feature declared, before its migrations
    run.

    Its own step, before `migrate`, because the privilege to create an extension
    is the one a store's database user routinely does not have — most managed
    PostgreSQL restricts it. Finding that out here means nothing has been changed
    yet and the message can name the statement an administrator must run; finding
    it out inside a migration means a half-applied schema and a rollback.

    Idempotent by construction: `IF NOT EXISTS`, every time. And nothing here
    ever drops one — an extension is shared with the store and with every other
    feature installed in the same database, so this step has no entry in the
    rollback table and no reverse to call.
    """
    declared = [name for name in (context.migrations.get("extensions") or []) if name]

    if not declared:
        return "the manifest declares no extensions"

    refused = [name for name in declared if name not in ALLOWED_EXTENSIONS]
    if refused:
        raise StepFailed(
            "extensions.refused",
            f"This store will not create {', '.join(sorted(refused))}. "
            f"It creates only: {', '.join(sorted(ALLOWED_EXTENSIONS))}.",
        )

    from django.db import connection

    if connection.vendor != "postgresql":
        raise StepFailed(
            "extensions.wrong_engine",
            f"{context.slug} needs the PostgreSQL extension(s) {', '.join(declared)} "
            f"and this store runs {connection.vendor}. The feature should not have been "
            "delivered here; its manifest declares 'database: postgresql'.",
        )

    created = []

    for name in declared:
        try:
            with connection.cursor() as cursor:
                # Interpolated because an extension name cannot be a bound
                # parameter. Safe because `name` has just been checked against a
                # frozenset of literals — nothing else can reach this line.
                cursor.execute(f'CREATE EXTENSION IF NOT EXISTS "{name}"')
        except Exception as exc:  # noqa: BLE001 - the reason matters more than the type
            raise StepFailed(
                "extensions.denied",
                f"The extension '{name}' could not be created: {exc}. "
                f"On most managed PostgreSQL this needs an administrator: ask for "
                f'`CREATE EXTENSION IF NOT EXISTS "{name}";` to be run once on this '
                "database, then retry the job. Nothing has been changed on this store.",
            ) from exc

        created.append(name)

    # Deliberately not appended to `context.applied`: there is nothing to undo,
    # and a rollback that dropped one of these could break a feature that has
    # been using it for a month (docs/adr/0031).
    return f"ensured {', '.join(created)}"


def migrate(context: JobContext) -> str:
    """
    Applies the feature's database migrations.

    Skipped entirely when the manifest declares none, and reported as skipped
    rather than succeeded so the job record says which it was. This is the step
    that can make a rollback impossible, so what it did is worth being precise
    about.
    """
    if not context.migrations.get("required"):
        return "the manifest declares no migrations"

    app_label = _installed_app_label(context)
    output = _run_django(["migrate", app_label, "--noinput"], context)
    context.applied.append("migrate")

    reversible = "reversible" if context.migrations.get("reversible") else "IRREVERSIBLE"
    return f"migrated {app_label} ({reversible}): {output}"


def configure(context: JobContext) -> str:
    """
    Writes the feature's configuration.

    Secrets are written with restrictive permissions and never logged or
    returned. The value of this step's output is the version number, not the
    contents (docs/feature-delivery.md §9).
    """
    configuration = context.job.get("configuration")
    if not configuration:
        return "no configuration to apply"

    target = context.target_dir / "knight_config.json"
    target.parent.mkdir(parents=True, exist_ok=True)

    import json

    document = {
        "version": configuration.get("version", 0),
        "values": json.loads(configuration.get("values") or "{}"),
        "secrets": configuration.get("secrets") or {},
    }

    target.write_text(json.dumps(document, indent=2), encoding="utf-8")

    try:
        target.chmod(0o600)
    except OSError:  # pragma: no cover - Windows and some mounts do not support it
        logger.debug("Could not restrict permissions on %s.", target)

    context.applied.append("configure")
    return f"configuration version {document['version']} applied"


def enable(context: JobContext) -> str:
    """Records the feature as installed and serving."""
    artifact = context.artifact
    configuration = context.job.get("configuration") or {}

    from datetime import datetime, timezone

    feature = InstalledFeature(
        slug=context.slug,
        version=context.version,
        app_label=_installed_app_label(context),
        installed_app=_installed_app(context),
        digest=artifact.get("digest", ""),
        installed_at=datetime.now(timezone.utc).isoformat(),
        enabled=True,
        config_version=int(configuration.get("version") or 0),
        url_include=_url_include(context),
        url_prefix=_url_prefix(context),
        workers=_workers(context),
    )

    context.registry.record(feature)
    context.applied.append("enable")

    return f"{feature.slug} {feature.version} enabled"


def disable(context: JobContext) -> str:
    """
    Stops the feature serving, leaving its code and data alone.

    Tolerant of a feature that is not installed: an entitlement can lapse for a
    store that never received the feature in the first place, and failing the job
    over it would leave a customer's account looking broken for no reason.
    """
    try:
        context.registry.set_enabled(context.slug, False)
    except KeyError:
        return f"{context.slug} is not installed; nothing to disable"

    context.applied.append("disable")
    return f"{context.slug} disabled; code and data retained"


def enable_existing(context: JobContext) -> str:
    """Re-enables an installed feature whose entitlement has come back."""
    try:
        context.registry.set_enabled(context.slug, True)
    except KeyError as exc:
        raise StepFailed("enable.not_installed", f"{context.slug} is not installed on this store.") from exc

    context.applied.append("enable")
    return f"{context.slug} re-enabled"


def remove_package(context: JobContext) -> str:
    """
    Deletes the feature's code.

    Only the code. Its database tables are left exactly where they are — KNIGHT
    holds the retention window and a separate purge removes the data when it
    expires (docs/feature-delivery.md §11).
    """
    target = context.target_dir

    if target.exists():
        shutil.rmtree(target)

    context.registry.remove(context.slug)
    context.applied.append("remove-package")

    return f"removed {target}; database tables retained"


def reload(context: JobContext) -> str:
    """
    Asks the store to pick up the change.

    Deliberately a no-op that reports honestly rather than something clever. How
    a store reloads is a deployment decision — a WSGI touch file, a systemd
    reload, a container restart — and guessing wrong means either dropping live
    requests or silently not reloading at all. The reload strategy is named in
    the deployment runbook and wired per environment.
    """
    touch_file = Path(context.config.feature_root) / "reload.trigger"

    try:
        touch_file.parent.mkdir(parents=True, exist_ok=True)
        touch_file.write_text(context.job.get("correlationId", ""), encoding="utf-8")
    except OSError as exc:
        raise StepFailed("reload.failed", f"The reload trigger could not be written: {exc}") from exc

    return "reload trigger written"


def healthcheck(context: JobContext) -> str:
    """
    Runs the feature's own health check, if it declared one.

    A feature that installs and then does not work is a failed install, not a
    successful one, and this is the step that tells the difference.
    """
    feature = context.registry.get(context.slug)
    check_path = feature.health_check if feature else None

    if not check_path:
        return "the feature declares no health check"

    module_name, _, attribute = check_path.rpartition(".")

    try:
        import importlib

        module = importlib.import_module(module_name)
        check = getattr(module, attribute)
        result = check()
    except Exception as exc:  # noqa: BLE001 - any failure here means unhealthy
        raise StepFailed("healthcheck.failed", f"The feature's health check raised: {exc}") from exc

    if result is False:
        raise StepFailed("healthcheck.unhealthy", "The feature's health check reported unhealthy.")

    return "healthy"


def restore_package(context: JobContext) -> str:
    """Puts back the package tree that `backup` kept."""
    backup_dir = context.workspace / "previous"

    if not backup_dir.exists():
        return "no previous version to restore"

    target = context.target_dir
    if target.exists():
        shutil.rmtree(target)

    shutil.copytree(backup_dir, target)

    if context.previous is not None:
        context.registry.record(context.previous)

    return f"restored {context.previous.version if context.previous else 'previous version'}"


def reverse_migrate(context: JobContext) -> str:
    """
    Undoes the feature's migrations, when the manifest says that is possible.

    When it is not, this raises rather than trying. KNIGHT turns that into
    `ManualInterventionRequired` and an incident, which is the honest outcome:
    guessing at how to reverse a migration that declared itself irreversible is
    how a rollback destroys data (docs/adr/0016).
    """
    if not context.migrations.get("required"):
        return "no migrations were applied"

    if not context.migrations.get("reversible"):
        raise StepFailed(
            "rollback.irreversible",
            f"{context.slug} {context.version} declares its migrations irreversible and they have already applied. "
            "KNIGHT will not guess; this store needs a human and a restore point.",
        )

    app_label = _installed_app_label(context)
    target = context.previous.version if context.previous else "zero"
    output = _run_django(["migrate", app_label, "zero" if context.previous is None else target, "--noinput"], context)

    return f"reversed {app_label} to {target}: {output}"


# --- Helpers ----------------------------------------------------------------


def _django(context: JobContext) -> dict[str, Any]:
    """
    The runtime wiring KNIGHT sends with the job: which module to load, what
    label its migrations are recorded under, and where to mount its URLs.

    Read from the job rather than guessed. The fallbacks below are a last resort
    for a job queued before KNIGHT carried this, and they are a guess: the slug
    with its hyphens swapped is the module name only by coincidence, and for
    every Feature in this repository it is wrong.
    """
    return context.job.get("django") or {}


def _installed_app(context: JobContext) -> str:
    """The importable module path."""
    return (
        _django(context).get("installedApp")
        or context.job.get("installedApp")
        or context.slug.replace("-", "_")
    )


def _installed_app_label(context: JobContext) -> str:
    return (
        _django(context).get("appLabel")
        or context.job.get("appLabel")
        or _installed_app(context).split(".")[-1]
    )


def _url_include(context: JobContext) -> str | None:
    """
    The feature's urlconf, or None when it serves no routes.

    A feature that declares one and does not get it recorded here is a feature
    whose pages 404 while every other part of the install reports success - which
    is precisely how this went unnoticed until phase 13 opened one in a browser.
    """
    return _django(context).get("urlInclude")


def _url_prefix(context: JobContext) -> str | None:
    return _django(context).get("urlPrefix")


def _workers(context: JobContext) -> list[dict[str, Any]]:
    """
    The scheduled jobs KNIGHT sent with this install.

    Normalised on the way in rather than trusted: a worker missing a name or an
    entrypoint is dropped here, where the install can still report it, instead
    of failing every hour afterwards inside a timer nobody is watching.
    """
    declared = _django(context).get("workers") or []
    kept: list[dict[str, Any]] = []

    for worker in declared:
        if not isinstance(worker, dict):
            continue

        name = worker.get("name")
        entrypoint = worker.get("entrypoint")

        if not name or not entrypoint:
            logger.warning(
                "Feature '%s' declared a worker with no %s; skipping it.",
                context.slug,
                "name" if not name else "entrypoint",
            )
            continue

        kept.append(
            {
                "name": str(name),
                "entrypoint": str(entrypoint),
                "schedule": str(worker.get("schedule") or "daily").lower(),
            }
        )

    return kept


def _run_django(arguments: list[str], context: JobContext) -> str:
    """
    Runs a Django management command in a subprocess.

    A subprocess rather than `call_command` in-process, because a migration must
    run against a fresh app registry that includes the feature just installed —
    and the process handling this job started before the feature existed. It also
    means a migration that crashes the interpreter cannot take the agent with it.

    The argument list is built here from fixed strings and the feature's own app
    label; nothing from the job body is ever passed to a shell.
    """
    command = [sys.executable, "manage.py", *arguments]

    try:
        completed = subprocess.run(  # noqa: S603 - fixed argv, never shell=True
            command,
            capture_output=True,
            text=True,
            timeout=1800,
            check=False,
            cwd=str(Path.cwd()),
        )
    except subprocess.TimeoutExpired as exc:
        raise StepFailed("migrate.timeout", "The migration did not finish within 30 minutes.") from exc

    if completed.returncode != 0:
        detail = (completed.stderr or completed.stdout or "").strip()[:2000]
        raise StepFailed("migrate.failed", f"`{' '.join(arguments)}` failed: {detail}")

    return (completed.stdout or "").strip().splitlines()[-1] if completed.stdout.strip() else "no output"


def _is_within(base: Path, candidate: Path) -> bool:
    try:
        candidate.resolve().relative_to(base.resolve())
    except ValueError:
        return False
    return True


def _extract_zip(path: Path, destination: Path) -> None:
    with zipfile.ZipFile(path) as archive:
        for member in archive.namelist():
            if not _is_within(destination, destination / member):
                raise StepFailed(
                    "install.unsafe_archive",
                    f"The archive contains '{member}', which would write outside the feature directory.",
                )

        archive.extractall(destination)


def _extract_tar(path: Path, destination: Path) -> None:
    with tarfile.open(path) as archive:
        for member in archive.getmembers():
            if member.issym() or member.islnk():
                raise StepFailed(
                    "install.unsafe_archive",
                    f"The archive contains a link ('{member.name}'), which could point anywhere on this machine.",
                )

            if not _is_within(destination, destination / member.name):
                raise StepFailed(
                    "install.unsafe_archive",
                    f"The archive contains '{member.name}', which would write outside the feature directory.",
                )

        archive.extractall(destination, filter="data")


def make_workspace(slug: str) -> Path:
    """A scratch directory for one job, cleaned up by the runner."""
    return Path(tempfile.mkdtemp(prefix=f"knight-{slug}-"))
