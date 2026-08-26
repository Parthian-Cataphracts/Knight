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

            self.stdout.write(
                self.style.SUCCESS(
                    f"  {feature.slug} {feature.version} -> {feature.installed_app}"
                    + ("" if feature.enabled else " (disabled)")
                )
            )

        self.stdout.write(f"Registry: {registry.path}")
        self.stdout.write("Restart the store for the app registry to pick this up.")

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
        return _read_simple_yaml(text)


def _read_simple_yaml(text: str) -> dict:
    """
    Enough YAML for a feature manifest, when PyYAML is not installed.

    Indentation-aware to any depth, because the schema is: `django.urls.include`
    is two levels down, and the version of this that handled exactly one level
    silently flattened it to `django.include`. Nothing noticed, because the
    caller was reading a third spelling that no manifest has ever used - so a
    Feature declaring routes was registered without them and served none of them
    (docs/phase-13-verification.md).

    Deliberately partial, and explicit about it: sequences and block scalars are
    skipped rather than guessed at. Nothing this command needs is inside one, and
    a parser that half-read a dependency list would be worse than one that
    admits it cannot. `pip install -r requirements-dev.txt` brings PyYAML, which
    is what actually parses this in development and in CI.
    """
    document: dict = {}

    # (indent, container) from the outside in. The last entry is where a key at
    # the current indent belongs.
    stack: list[tuple[int, dict]] = [(-1, document)]

    # Set while inside a block scalar or a sequence, to the indent of the line
    # that opened it: everything more indented than this belongs to it.
    skipping_deeper_than: int | None = None

    for raw in text.splitlines():
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue

        indent = len(raw) - len(raw.lstrip(" "))

        if skipping_deeper_than is not None:
            if indent > skipping_deeper_than:
                continue
            skipping_deeper_than = None

        stripped = raw.strip()

        # A sequence item.
        #
        # Inline maps are kept, because `workers:` is a list of them and a
        # feature whose scheduled jobs were silently dropped would install
        # cleanly and then never run them - the same class of failure as the
        # urls block being flattened. Block-style items are still skipped: this
        # parser is the fallback, PyYAML is what runs in development and CI, and
        # a parser that half-read a sequence would be worse than one that admits
        # it cannot.
        if stripped.startswith("- "):
            item = stripped[2:].strip()

            if item.startswith("{") and item.endswith("}") and stack:
                holder = stack[-1][1]
                key = _last_key(holder)

                if key is not None and isinstance(holder.get(key), dict) and not holder[key]:
                    holder[key] = []

                if key is not None and isinstance(holder.get(key), list):
                    holder[key].append(_read_inline_map(item))

                continue

            skipping_deeper_than = indent
            continue

        if ":" not in stripped:
            continue

        key, _, value = stripped.partition(":")
        key = key.strip()
        value = value.strip()

        while stack and indent <= stack[-1][0]:
            stack.pop()

        if not stack:
            stack = [(-1, document)]

        parent = stack[-1][1]

        # A block scalar: the value is the indented lines that follow, and this
        # command needs none of them.
        if value in (">", ">-", "|", "|-", ">+", "|+"):
            parent[key] = ""
            skipping_deeper_than = indent
            continue

        if value.startswith("{") and value.endswith("}"):
            parent[key] = _read_inline_map(value)
            continue

        if value:
            parent[key] = value.strip('"').strip("'")
            continue

        child: dict = {}
        parent[key] = child
        stack.append((indent, child))

    return document


def _last_key(holder: dict):
    """The key most recently opened on this mapping, or None when there is none."""
    for key in reversed(list(holder)):
        return key

    return None


def _read_inline_map(value: str) -> dict:
    """A single-line `{ key: value, key: value }` map, which the schema also allows."""
    inner = value.strip()[1:-1]
    result: dict = {}

    for part in inner.split(","):
        if ":" not in part:
            continue

        key, _, item = part.partition(":")
        result[key.strip()] = item.strip().strip('"').strip("'")

    return result
