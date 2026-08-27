"""
`manage.py knight_install_local` — register a feature package that is already
on this machine, without going through KNIGHT.

The normal path to an installed feature is a delivery job: KNIGHT publishes a
signed artifact, the store downloads it, verifies the signature and installs it.
That path is the product, and nothing here replaces it.

It is, however, the wrong path for two cases that are not the product:

- a developer working on a feature out of the source tree, who wants the store
  to load the code they are editing rather than a packaged copy of it;
- CI, which installs both optional Features with `pip install` and would
  otherwise run their tests against a store where the app is not installed —
  every one of those tests skipping, and the suite going green having checked
  nothing.

The second case is the reason this exists as a command rather than as a
paragraph in a README telling people to write `installed.json` by hand. A
registry file that only ever comes into being by hand is a registry file that is
different on every machine.

The digest recorded is `sha256:local-development`, which is not a real digest and
is not meant to look like one. Drift detection compares it against what KNIGHT
published and will report it as drift, which is correct: this store is running
code KNIGHT never delivered.
"""

from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path

from django.core.management.base import BaseCommand, CommandError

from ...installer.state import InstalledFeature, get_registry

#: Deliberately not a hash of anything. See the module docstring.
LOCAL_DIGEST = "sha256:local-development"


class Command(BaseCommand):
    help = "Registers a locally present feature package in this store's feature registry."

    def add_arguments(self, parser) -> None:
        parser.add_argument(
            "source",
            nargs="+",
            help="Path to a feature source directory — the one holding knight_manifest.yaml.",
        )
        parser.add_argument(
            "--disabled",
            action="store_true",
            help="Register the feature but leave it switched off, as a lapsed entitlement would.",
        )

    def handle(self, *args, **options) -> None:
        registry = get_registry()

        for raw_source in options["source"]:
            manifest = self._manifest(Path(raw_source))
            feature = self._feature(manifest, enabled=not options["disabled"])

            registry.record(feature)
            self._extensions(manifest)

            self.stdout.write(
                self.style.SUCCESS(
                    f"  {feature.slug} {feature.version} -> {feature.installed_app}"
                    + ("" if feature.enabled else " (disabled)")
                )
            )

        self.stdout.write(f"Registry: {registry.path}")
        self.stdout.write("Restart the store for the app registry to pick this up.")

    def _extensions(self, manifest: dict) -> None:
        """
        Creates the database extensions the manifest declares.

        The real installer does this in its own step before migrating; this
        command is the other path to an installed Feature and has to do the same
        thing, or a developer's first `migrate` fails on an operator class that
        does not exist. Idempotent and never dropped again, exactly as there
        (docs/adr/0031-database-extensions-are-declared-not-migrated.md).

        Refused rather than created when the name is not one this store's
        installer would accept: the same closed list, so that the shortcut cannot
        do something the product path would not.
        """
        declared = [name for name in ((manifest.get("migrations") or {}).get("extensions") or []) if name]

        if not declared:
            return

        from django.db import connection

        from ...installer.steps import ALLOWED_EXTENSIONS

        refused = sorted(name for name in declared if name not in ALLOWED_EXTENSIONS)
        if refused:
            raise CommandError(
                f"This store will not create {', '.join(refused)}. "
                f"It creates only: {', '.join(sorted(ALLOWED_EXTENSIONS))}."
            )

        if connection.vendor != "postgresql":
            raise CommandError(
                f"{manifest.get('slug')} needs the PostgreSQL extension(s) "
                f"{', '.join(declared)} and this store runs {connection.vendor}."
            )

        for name in declared:
            try:
                with connection.cursor() as cursor:
                    # Interpolated because an extension name cannot be a bound
                    # parameter; safe because the list above is a frozenset of
                    # literals.
                    cursor.execute(f'CREATE EXTENSION IF NOT EXISTS "{name}"')
            except Exception as exc:  # noqa: BLE001 - the reason matters more than the type
                raise CommandError(
                    f"The extension '{name}' could not be created: {exc}. Run "
                    f'`CREATE EXTENSION IF NOT EXISTS "{name}";` as an administrator '
                    "on this database and try again."
                ) from exc

            self.stdout.write(f"  extension {name} ensured")

    def _manifest(self, source: Path) -> dict:
        if not source.is_dir():
            raise CommandError(f"{source} is not a directory.")

        path = source / "knight_manifest.yaml"
        if not path.exists():
            raise CommandError(f"No knight_manifest.yaml in {source}.")

        return _read_manifest(path)

    def _feature(self, manifest: dict, *, enabled: bool) -> InstalledFeature:
        django_section = manifest.get("django") or {}
        install_section = manifest.get("install") or {}

        # Nested, as the manifest format declares it and as KNIGHT's own
        # ManifestReader parses it. This used to read flat `url_include` and
        # `url_prefix` keys that no manifest has ever had, so a Feature declaring
        # routes was registered without them and served none of them.
        urls_section = django_section.get("urls") or {}

        # Workers are a top-level block, not part of the django one: they are a
        # scheduling fact rather than a framework-integration fact, and a
        # non-Django store would still have them (docs/risks.md R26).
        declared_workers = manifest.get("workers") or []

        slug = manifest.get("slug")
        installed_app = django_section.get("installed_app")

        # Checked rather than defaulted: a feature registered under the wrong app
        # name fails at startup, a long way from the mistake that caused it.
        if not slug:
            raise CommandError("The manifest declares no slug.")
        if not installed_app:
            raise CommandError(f"The manifest for '{slug}' declares no django.installed_app.")

        return InstalledFeature(
            slug=slug,
            version=str(manifest.get("version", "")),
            app_label=django_section.get("app_label", ""),
            installed_app=installed_app,
            digest=LOCAL_DIGEST,
            installed_at=datetime.now(timezone.utc).isoformat(),
            enabled=enabled,
            url_include=urls_section.get("include"),
            url_prefix=urls_section.get("prefix"),
            workers=[
                {
                    "name": str(worker.get("name", "")),
                    "entrypoint": str(worker.get("entrypoint", "")),
                    "schedule": str(worker.get("schedule", "daily")).lower(),
                }
                for worker in declared_workers
                if isinstance(worker, dict) and worker.get("name") and worker.get("entrypoint")
            ],
            health_check=install_section.get("healthCheck"),
        )


def _read_manifest(path: Path) -> dict:
    """
    The manifest, parsed with PyYAML when it is present and by hand when it is
    not. The store does not depend on PyYAML — nothing it does in production
    reads YAML — and this command is not a reason to make it.
    """
    text = path.read_text(encoding="utf-8")

    try:
        import yaml  # type: ignore

        return yaml.safe_load(text) or {}
    except ImportError:
        try:
            return _read_simple_yaml(text)
        except ManifestUnreadable as exc:
            raise CommandError(
                f"{path}: {exc}\n"
                "This store reads manifests with a small built-in parser when PyYAML is not "
                "installed, and it refuses shapes it cannot read rather than dropping them. "
                "Install PyYAML (`pip install -r requirements-dev.txt`) and run this again."
            ) from exc


class ManifestUnreadable(ValueError):
    """A manifest shape this fallback reader will not guess at."""


def _read_simple_yaml(text: str) -> dict:
    """
    Enough YAML for a feature manifest, when PyYAML is not installed.

    Indentation-aware to any depth, because the schema is: `django.urls.include`
    is two levels down, and the version of this that handled exactly one level
    silently flattened it to `django.include`. Nothing noticed, because the
    caller was reading a third spelling that no manifest has ever used - so a
    Feature declaring routes was registered without them and served none of them
    (docs/phase-13-verification.md).

    It reads sequences too, which the version before phase 16 claimed to and did
    not: every manifest in this repository writes `workers:` as block-style
    mapping items, and every one of them came back as an empty mapping. The
    effect was the phase-13 failure again one field along - a Feature registered
    with no scheduled jobs, installing cleanly and then never running them - and
    it stayed invisible because PyYAML is installed in development and in CI, so
    this code path only ever runs on a bare store.

    Phase 16 found the fourth of these, in the same shape again: an inline
    sequence — `extensions: []`, which is how a Feature says it needs no database
    extension — came back as the two-character string `"[]"`. Truthy, iterable,
    and one character from being a list of one extension called `[`.

    Still deliberately partial, and now loud about it. A shape it does not
    understand raises `ManifestUnreadable` rather than skipping the line: a
    reader that silently drops what it cannot parse is exactly how the last two
    of these went unnoticed.
    """
    document: dict = {}

    # (indent, container) from the outside in, where a container is the mapping
    # or list that a line at the current indent belongs to. The root sits at -1
    # so that a top-level key at indent 0 finds it.
    stack: list[tuple[int, object]] = [(-1, document)]

    # Set while inside a block scalar, to the indent of the line that opened it:
    # everything more indented belongs to the scalar and is not read.
    skipping_deeper_than: int | None = None

    lines = text.splitlines()

    for position, raw in enumerate(lines):
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue

        indent = len(raw) - len(raw.lstrip(" "))

        if skipping_deeper_than is not None:
            if indent > skipping_deeper_than:
                continue
            skipping_deeper_than = None

        stripped = raw.strip()

        while len(stack) > 1 and indent <= stack[-1][0]:
            stack.pop()

        container = stack[-1][1]

        # A sequence item: a scalar (`- pg_trgm`), an inline map
        # (`- { slug: x, version: y }`) or a block-style mapping whose remaining
        # keys are the more-indented lines that follow (`- name: x` then
        # `  entrypoint: y`). All three appear in the manifests in this
        # repository, and dropping any of them loses something an install needs.
        if stripped.startswith("- "):
            if not isinstance(container, list):
                raise ManifestUnreadable(f"A list item appears where no list was opened: {stripped}")

            item = stripped[2:].strip()

            if item.startswith("{") and item.endswith("}"):
                container.append(_read_inline_map(item))
                continue

            if ":" not in item:
                container.append(_unquote(item))
                continue

            entry: dict = {}
            entry_key, _, entry_value = item.partition(":")

            if not entry_value.strip():
                # `- key:` with the value on the lines below. Rare, absent from
                # every manifest here, and refused rather than guessed at.
                raise ManifestUnreadable(f"Cannot read manifest line: {stripped}")

            entry[entry_key.strip()] = _unquote(entry_value.strip())
            container.append(entry)
            stack.append((indent, entry))
            continue

        if ":" not in stripped:
            raise ManifestUnreadable(f"Cannot read manifest line: {stripped}")

        if not isinstance(container, dict):
            raise ManifestUnreadable(f"A mapping key appears inside a list: {stripped}")

        key, _, value = stripped.partition(":")
        key = key.strip()
        value = value.strip()

        # A block scalar: the value is the indented lines that follow, and this
        # command needs none of them.
        if value in (">", ">-", "|", "|-", ">+", "|+"):
            container[key] = ""
            skipping_deeper_than = indent
            continue

        if value.startswith("{") and value.endswith("}"):
            container[key] = _read_inline_map(value)
            continue

        if value.startswith("[") and value.endswith("]"):
            container[key] = _read_inline_list(value)
            continue

        if value:
            container[key] = _unquote(value)
            continue

        # An empty value opens either a mapping or a list, and only the next
        # meaningful line says which.
        child: object = [] if _next_meaningful(lines, position + 1).lstrip().startswith("- ") else {}
        container[key] = child
        stack.append((indent, child))

    return document


def _next_meaningful(lines: list[str], start: int) -> str:
    """The next line that is neither blank nor a comment, or an empty string."""
    for line in lines[start:]:
        if line.strip() and not line.lstrip().startswith("#"):
            return line

    return ""


def _unquote(value: str):
    """
    A scalar, as its type rather than as text.

    `true` read as the string "true" is truthy either way, which is exactly why
    this is worth doing: the field where it stops being harmless is the one
    nobody has written yet. `migrations.reversible` is the obvious candidate —
    `"false"` is true.
    """
    value = value.strip()

    if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
        return value[1:-1]

    lowered = value.lower()

    if lowered in ("true", "yes"):
        return True
    if lowered in ("false", "no"):
        return False
    if lowered in ("null", "~"):
        return None

    try:
        return int(value)
    except ValueError:
        pass

    try:
        return float(value)
    except ValueError:
        return value


def _read_inline_list(value: str) -> list:
    """
    A single-line `[a, b]` sequence, which the schema also allows.

    Written for `extensions: []` — the way a Feature says it needs no database
    extension — and correct for a populated one too. The empty case is the one
    that matters and the one that was wrong: read as the string `"[]"` it is
    truthy, so a caller asking "does this Feature declare extensions" got yes.
    """
    inner = value.strip()[1:-1].strip()

    if not inner:
        return []

    return [_unquote(part) for part in _split_outside_quotes(inner) if part.strip()]


def _read_inline_map(value: str) -> dict:
    """A single-line `{ key: value, key: value }` map, which the schema also allows."""
    result: dict = {}

    for part in _split_outside_quotes(value.strip()[1:-1]):
        if ":" not in part:
            continue

        key, _, item = part.partition(":")
        result[key.strip()] = _unquote(item)

    return result


def _split_outside_quotes(text: str) -> list[str]:
    """
    Splits on commas that are not inside a quoted string.

    A plain `text.split(",")` tears a version range in half: the dependency
    `{ slug: analytics-core, version: ">=1.0.0,<2.0.0" }` became a slug, a
    version of `">=1.0.0`, and a third key called `<2.0.0"`. The packaging tool
    had the identical bug and it was fixed there in phase 15; this copy was not
    found at the time because nothing this command reads is inside an inline map
    (docs/phase-15-verification.md).
    """
    parts: list[str] = []
    current: list[str] = []
    quote: str | None = None

    for character in text:
        if quote is not None:
            current.append(character)

            if character == quote:
                quote = None

            continue

        if character in "\"'":
            quote = character
            current.append(character)
            continue

        if character == ",":
            parts.append("".join(current))
            current = []
            continue

        current.append(character)

    parts.append("".join(current))

    return parts
