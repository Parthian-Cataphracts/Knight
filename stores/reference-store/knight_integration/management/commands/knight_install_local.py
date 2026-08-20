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
            url_include=django_section.get("url_include"),
            url_prefix=django_section.get("url_prefix"),
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
    Enough YAML for a feature manifest: top-level scalars and one level of
    nesting, which is all the schema allows. Anything else is ignored rather
    than guessed at.
    """
    document: dict = {}
    section: dict | None = None

    for line in text.splitlines():
        if not line.strip() or line.lstrip().startswith("#"):
            continue

        indented = line[0] in " \t"
        stripped = line.strip()

        if ":" not in stripped:
            continue

        key, _, value = stripped.partition(":")
        key = key.strip()
        value = value.strip().strip('"').strip("'")

        if indented:
            if section is not None and value:
                section[key] = value
            continue

        if value:
            document[key] = value
            section = None
        else:
            section = {}
            document[key] = section

    return document
