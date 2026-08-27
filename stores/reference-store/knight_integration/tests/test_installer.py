"""
The installer: verification, the on-disk registry, and how a job fails.

The verification tests are the ones that matter most. Everything downstream of
them assumes the bytes are what KNIGHT published, so a hole here is a hole in the
whole delivery model — which is why they are written as attacks rather than as
happy paths.
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import shutil
import tempfile
import zipfile
from pathlib import Path
from unittest import mock

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from django.test import SimpleTestCase, TestCase

from knight_integration.installer.runner import KNOWN_JOB_TYPES, STEP_IMPLEMENTATIONS, JobRunner
from knight_integration.installer.state import InstallationRegistry, InstalledFeature
from knight_integration.installer.verify import (
    ArtifactRejected,
    compute_digest,
    verify_artifact,
    verify_digest,
    verify_signature,
)


def _key_pair() -> tuple[ec.EllipticCurvePrivateKey, str]:
    """A P-256 pair, with the public half in the base64 DER form configuration uses."""
    private = ec.generate_private_key(ec.SECP256R1())
    public_der = private.public_key().public_bytes(
        encoding=serialization.Encoding.DER,
        format=serialization.PublicFormat.SubjectPublicKeyInfo,
    )
    return private, base64.b64encode(public_der).decode("ascii")


def _sign(private: ec.EllipticCurvePrivateKey, digest: str) -> str:
    return base64.b64encode(
        private.sign(digest.encode("ascii"), ec.ECDSA(hashes.SHA256()))
    ).decode("ascii")


class ArtifactVerificationTests(SimpleTestCase):
    """What the store will and will not accept as installable code."""

    def setUp(self) -> None:
        self.workspace = Path(tempfile.mkdtemp(prefix="knight-verify-"))
        self.addCleanup(shutil.rmtree, self.workspace, ignore_errors=True)

        self.artifact = self.workspace / "feature.zip"
        self.artifact.write_bytes(b"a plausible feature wheel")

        self.digest = hashlib.sha256(self.artifact.read_bytes()).hexdigest()
        self.private, self.public = _key_pair()
        self.keys = {"dev": self.public}

    def test_a_good_artifact_is_accepted(self):
        signature = _sign(self.private, self.digest)

        result = verify_artifact(self.artifact, self.digest, signature, "dev", self.keys)

        self.assertEqual(self.digest, result)

    def test_the_digest_is_computed_from_the_bytes_on_disk(self):
        self.assertEqual(self.digest, compute_digest(self.artifact))

    def test_a_tampered_artifact_is_refused(self):
        # The attacker changed the file but not the digest KNIGHT published.
        self.artifact.write_bytes(b"something else entirely")

        with self.assertRaises(ArtifactRejected) as caught:
            verify_digest(self.artifact, self.digest)

        self.assertEqual("digest.mismatch", caught.exception.code)

    def test_an_artifact_with_no_declared_digest_is_refused(self):
        with self.assertRaises(ArtifactRejected) as caught:
            verify_digest(self.artifact, "")

        self.assertEqual("digest.missing", caught.exception.code)

    def test_an_unsigned_artifact_is_refused(self):
        with self.assertRaises(ArtifactRejected) as caught:
            verify_signature(self.digest, "", "dev", self.keys)

        self.assertEqual("signature.missing", caught.exception.code)

    def test_an_artifact_signed_by_an_unknown_key_is_refused(self):
        # The whole attack this stops: substitute the artifact, substitute the
        # digest, sign it with your own key. Only the store's own key list saves
        # it.
        attacker, _ = _key_pair()
        signature = _sign(attacker, self.digest)

        with self.assertRaises(ArtifactRejected) as caught:
            verify_signature(self.digest, signature, "attacker-key", self.keys)

        self.assertEqual("signature.unknown_key", caught.exception.code)

    def test_a_signature_from_the_wrong_key_is_refused(self):
        attacker, _ = _key_pair()
        signature = _sign(attacker, self.digest)

        with self.assertRaises(ArtifactRejected) as caught:
            verify_signature(self.digest, signature, "dev", self.keys)

        self.assertEqual("signature.invalid", caught.exception.code)

    def test_a_signature_over_a_different_digest_is_refused(self):
        signature = _sign(self.private, "0" * 64)

        with self.assertRaises(ArtifactRejected) as caught:
            verify_signature(self.digest, signature, "dev", self.keys)

        self.assertEqual("signature.invalid", caught.exception.code)

    def test_a_malformed_signature_is_refused_rather_than_raising(self):
        with self.assertRaises(ArtifactRejected) as caught:
            verify_signature(self.digest, "not base64 at all!!", "dev", self.keys)

        self.assertIn("signature.", caught.exception.code)

    def test_verification_checks_the_digest_before_the_signature(self):
        # A signature over a digest that does not match the file proves KNIGHT
        # signed *a* digest and says nothing about these bytes, so the digest
        # failure must be the one reported.
        signature = _sign(self.private, self.digest)
        self.artifact.write_bytes(b"replaced after signing")

        with self.assertRaises(ArtifactRejected) as caught:
            verify_artifact(self.artifact, self.digest, signature, "dev", self.keys)

        self.assertEqual("digest.mismatch", caught.exception.code)


class InstallationRegistryTests(SimpleTestCase):
    """The store's own record of what is on disk."""

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="knight-registry-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        self.registry = InstallationRegistry(self.root)

    def _feature(self, slug="analytics-core", version="1.0.0", enabled=True) -> InstalledFeature:
        return InstalledFeature(
            slug=slug,
            version=version,
            app_label=slug.replace("-", "_"),
            installed_app=slug.replace("-", "_"),
            digest="a" * 64,
            installed_at="2026-08-19T12:00:00+00:00",
            enabled=enabled,
        )

    def test_a_fresh_store_has_nothing_installed(self):
        self.assertEqual({}, self.registry.load())

    def test_a_recorded_feature_survives_a_round_trip(self):
        self.registry.record(self._feature())

        loaded = self.registry.load()

        self.assertEqual("1.0.0", loaded["analytics-core"].version)
        self.assertTrue(loaded["analytics-core"].enabled)

    def test_disabling_keeps_the_feature_and_its_version(self):
        # Losing an entitlement disables; it never removes. The record has to
        # show the code is still there.
        self.registry.record(self._feature())

        self.registry.set_enabled("analytics-core", False)

        feature = self.registry.get("analytics-core")
        self.assertIsNotNone(feature)
        self.assertFalse(feature.enabled)
        self.assertEqual("1.0.0", feature.version)

    def test_only_enabled_features_are_loaded_into_django(self):
        self.registry.record(self._feature("analytics-core"))
        self.registry.record(self._feature("analytics-reports", enabled=False))

        enabled = [feature.slug for feature in self.registry.enabled_features()]

        self.assertEqual(["analytics-core"], enabled)

    def test_removing_a_feature_takes_it_out_of_the_record(self):
        self.registry.record(self._feature())

        self.registry.remove("analytics-core")

        self.assertEqual({}, self.registry.load())

    def test_disabling_something_absent_is_an_error_rather_than_a_silent_write(self):
        with self.assertRaises(KeyError):
            self.registry.set_enabled("never-installed", False)

    def test_a_corrupt_registry_is_refused_rather_than_read_as_empty(self):
        # Treating an unreadable file as "nothing installed" would make the
        # installer reinstall packages that are already on disk.
        self.registry.path.parent.mkdir(parents=True, exist_ok=True)
        self.registry.path.write_text("{ this is not json", encoding="utf-8")

        with self.assertRaises(RuntimeError):
            self.registry.load()

    def test_the_file_is_readable_by_a_human_during_an_incident(self):
        self.registry.record(self._feature())

        document = json.loads(self.registry.path.read_text(encoding="utf-8"))

        self.assertEqual(1, document["schemaVersion"])
        self.assertIn("analytics-core", document["features"])

    def test_a_write_leaves_no_temporary_files_behind(self):
        self.registry.record(self._feature())

        leftovers = [path.name for path in self.root.iterdir() if path.name.startswith(".installed-")]

        self.assertEqual([], leftovers)


class _StubClient:
    """Records what the runner reported, so the tests can assert on it."""

    def __init__(self, job=None):
        self._job = job
        self.steps: list[tuple[str, str, str | None]] = []
        self.completion: dict | None = None

    def claim_job(self):
        return self._job

    def report_step(self, job_id, step, status, output=None, error_code=None, duration_ms=None):
        self.steps.append((step, status, error_code))

    def complete_job(self, job_id, succeeded, failure_code=None, failure_message=None,
                     rollback_outcome=None, installed_version=None, health=None):
        self.completion = {
            "succeeded": succeeded,
            "failureCode": failure_code,
            "rollbackOutcome": rollback_outcome,
        }


class JobRunnerTests(SimpleTestCase):
    """What the runner will and will not do."""

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="knight-runner-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

    def _config(self):
        from knight_integration.conf import get_settings

        config = get_settings()
        object.__setattr__(config, "feature_root", str(self.root))
        return config

    def test_an_unknown_job_type_is_refused_rather_than_attempted(self):
        # The property that keeps this agent from being a remote shell: it does
        # what it recognises and nothing else.
        client = _StubClient()
        runner = JobRunner(client=client, config=self._config())

        outcome = runner.execute({"jobId": "1", "type": "RunArbitraryCommand", "featureSlug": "x", "steps": []})

        self.assertFalse(outcome.succeeded)
        self.assertEqual("job.unknown_type", outcome.failure_code)
        self.assertEqual("job.unknown_type", client.completion["failureCode"])

    def test_an_unknown_step_is_refused_rather_than_guessed_at(self):
        client = _StubClient()
        runner = JobRunner(client=client, config=self._config())

        outcome = runner.execute({
            "jobId": "1",
            "type": "Install",
            "featureSlug": "analytics-core",
            "steps": ["preflight", "exfiltrate-database"],
            "artifact": {"sizeBytes": 10, "downloadUrl": "http://localhost/x"},
        })

        self.assertFalse(outcome.succeeded)
        self.assertEqual("job.unknown_step", outcome.failure_code)

    def test_no_queued_job_is_not_an_error(self):
        runner = JobRunner(client=_StubClient(job=None), config=self._config())

        self.assertIsNone(runner.run_once())

    def test_every_job_type_knight_can_queue_has_an_implementation(self):
        # If KNIGHT grows a job type and the agent does not, the agent refuses
        # it — but that should be caught here, not in production.
        self.assertEqual(
            {"Install", "Upgrade", "ApplyConfiguration", "Enable", "Disable", "Uninstall", "Rollback"},
            set(KNOWN_JOB_TYPES),
        )

    def test_every_step_knight_names_has_an_implementation(self):
        expected = {
            "preflight", "fetch", "verify", "backup", "install", "create-extensions",
            "migrate", "configure", "enable", "reload", "healthcheck", "disable",
            "remove-package", "restore-package", "reverse-migrate",
        }

        self.assertEqual(expected, set(STEP_IMPLEMENTATIONS))

    def test_a_failing_install_reports_a_rollback_outcome(self):
        client = _StubClient()
        runner = JobRunner(client=client, config=self._config())

        # No download URL, so fetch fails before anything has been changed.
        outcome = runner.execute({
            "jobId": "1",
            "type": "Install",
            "featureSlug": "analytics-core",
            "targetVersion": "1.0.0",
            "steps": ["preflight", "fetch"],
            "artifact": {"sizeBytes": 10, "digest": "a" * 64},
        })

        self.assertFalse(outcome.succeeded)
        self.assertEqual("fetch.no_url", outcome.failure_code)

        # Nothing had been applied, so there was nothing to undo — which is a
        # different outcome from a rollback that ran.
        self.assertEqual("NotAttempted", outcome.rollback_outcome)

    def test_preflight_refuses_an_artifact_larger_than_the_store_allows(self):
        client = _StubClient()
        config = self._config()
        object.__setattr__(config, "max_artifact_bytes", 1024)
        runner = JobRunner(client=client, config=config)

        outcome = runner.execute({
            "jobId": "1",
            "type": "Install",
            "featureSlug": "analytics-core",
            "steps": ["preflight"],
            "artifact": {"sizeBytes": 999_999_999, "downloadUrl": "http://localhost/x"},
        })

        self.assertFalse(outcome.succeeded)
        self.assertEqual("preflight.too_large", outcome.failure_code)


class ArchiveSafetyTests(SimpleTestCase):
    """An archive is untrusted input even after its signature checks out."""

    def setUp(self) -> None:
        self.workspace = Path(tempfile.mkdtemp(prefix="knight-archive-"))
        self.addCleanup(shutil.rmtree, self.workspace, ignore_errors=True)

    def test_a_zip_that_escapes_its_directory_is_refused(self):
        from knight_integration.installer.steps import StepFailed, _extract_zip

        archive = self.workspace / "evil.zip"
        with zipfile.ZipFile(archive, "w") as handle:
            handle.writestr("../../escaped.py", "print('pwned')")

        destination = self.workspace / "target"
        destination.mkdir()

        with self.assertRaises(StepFailed) as caught:
            _extract_zip(archive, destination)

        self.assertEqual("install.unsafe_archive", caught.exception.code)

    def test_a_well_behaved_zip_extracts(self):
        from knight_integration.installer.steps import _extract_zip

        archive = self.workspace / "good.zip"
        with zipfile.ZipFile(archive, "w") as handle:
            handle.writestr("analytics_core/__init__.py", "")

        destination = self.workspace / "target"
        destination.mkdir()

        _extract_zip(archive, destination)

        self.assertTrue((destination / "analytics_core" / "__init__.py").exists())


class RuntimeWiringTests(SimpleTestCase):
    """
    What the `enable` step records about how to load the package.

    This is the regression pinned after phase 13: none of it was recorded at all.
    The step took the module name from the slug with its hyphens swapped, which is
    the right answer only while the two happen to match — and once `adr/0029`
    shortened every slug they stopped matching. A Feature that declared routes
    was registered without them, so its pages 404'd while every other step of the
    install reported success.
    """

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="knight-wiring-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

    def _enabled(self, job: dict):
        from knight_integration.conf import get_settings
        from knight_integration.installer.state import get_registry
        from knight_integration.installer.steps import JobContext, enable

        config = get_settings()
        object.__setattr__(config, "feature_root", str(self.root))

        registry = get_registry(self.root)
        context = JobContext(job=job, config=config, registry=registry, workspace=self.root)
        enable(context)

        return registry.load()[job["featureSlug"]]

    def _job(self, **django) -> dict:
        return {
            "jobId": "1",
            "type": "Install",
            "featureSlug": "reviews-ratings",
            "targetVersion": "1.0.0",
            "steps": ["enable"],
            "artifact": {"digest": "sha256:abc"},
            "django": django,
        }

    def test_the_module_to_load_comes_from_the_job_not_from_the_slug(self):
        # The slug is `reviews-ratings`; the module is not `reviews_ratings`.
        feature = self._enabled(
            self._job(
                appLabel="knight_reviews",
                installedApp="knight_feature_reviews_ratings",
            )
        )

        self.assertEqual(feature.installed_app, "knight_feature_reviews_ratings")
        self.assertEqual(feature.app_label, "knight_reviews")

    def test_a_feature_that_declares_routes_gets_them_recorded(self):
        feature = self._enabled(
            self._job(
                appLabel="knight_reviews",
                installedApp="knight_feature_reviews_ratings",
                urlInclude="knight_feature_reviews_ratings.urls",
                urlPrefix="reviews/",
            )
        )

        self.assertEqual(feature.url_include, "knight_feature_reviews_ratings.urls")
        self.assertEqual(feature.url_prefix, "reviews/")

    def test_a_feature_that_serves_no_routes_records_none(self):
        # None rather than a default prefix: the loader mounts nothing for a
        # feature with no urlconf, and inventing one would mount an import error.
        feature = self._enabled(
            self._job(appLabel="knight_analytics_core", installedApp="knight_feature_analytics_core")
        )

        self.assertIsNone(feature.url_include)
        self.assertIsNone(feature.url_prefix)

    def test_a_job_from_before_knight_sent_this_still_installs(self):
        # The fallback is a guess and is documented as one. It must still be a
        # guess rather than a crash, because a job queued by an older KNIGHT is
        # a job that has already been paid for.
        job = self._job()
        job.pop("django")

        feature = self._enabled(job)

        self.assertEqual(feature.installed_app, "reviews_ratings")
        self.assertIsNone(feature.url_include)


class ExtensionStepTests(TestCase):
    """
    Creating the database extensions a Feature declared.

    Its own step, running before `migrate`, because the privilege to create an
    extension is the one a store's database user routinely does not have. What
    this class is mostly about is what it refuses: the job body is not signed, so
    the list of extensions this store will create is held here rather than taken
    from whatever a job asks for
    (docs/adr/0031-database-extensions-are-declared-not-migrated.md).
    """

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="knight-extensions-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

    def _context(self, extensions):
        from knight_integration.conf import get_settings
        from knight_integration.installer.state import get_registry
        from knight_integration.installer.steps import JobContext

        config = get_settings()
        object.__setattr__(config, "feature_root", str(self.root))

        job = {
            "jobId": "1",
            "type": "Install",
            "featureSlug": "advanced-search",
            "targetVersion": "1.1.0",
            "migrations": {"required": True, "reversible": True, "extensions": extensions},
        }

        return JobContext(job=job, config=config, registry=get_registry(self.root), workspace=self.root)

    def test_a_feature_that_declares_none_is_skipped_rather_than_run(self):
        from knight_integration.installer.steps import create_extensions

        output = create_extensions(self._context([]))

        # The wording matters: the runner reads it to report Skipped rather than
        # Succeeded, and the job record should say which it was.
        self.assertTrue(output.startswith("the manifest declares no"))

    def test_a_declared_extension_is_created(self):
        from django.db import connection

        from knight_integration.installer.steps import create_extensions

        if connection.vendor != "postgresql":
            self.skipTest("Extensions are a PostgreSQL concept.")

        create_extensions(self._context(["unaccent"]))

        with connection.cursor() as cursor:
            cursor.execute("SELECT count(*) FROM pg_extension WHERE extname = 'unaccent'")
            self.assertEqual(1, cursor.fetchone()[0])

    def test_creating_one_twice_is_not_an_error(self):
        from django.db import connection

        from knight_integration.installer.steps import create_extensions

        if connection.vendor != "postgresql":
            self.skipTest("Extensions are a PostgreSQL concept.")

        # An agent that lost a reply re-runs the step. Every step in this
        # pipeline is idempotent and this one is no exception.
        create_extensions(self._context(["unaccent"]))
        output = create_extensions(self._context(["unaccent"]))

        self.assertIn("unaccent", output)

    def test_an_extension_outside_the_stores_own_list_is_refused(self):
        from knight_integration.installer.steps import StepFailed, create_extensions

        # The whole reason the store keeps its own copy of the list. A job body
        # is not signed, so a control plane that has been compromised - or has
        # simply grown a field this store does not understand - must not be able
        # to talk this store into loading a procedural language.
        with self.assertRaises(StepFailed) as raised:
            create_extensions(self._context(["plpython3u"]))

        self.assertEqual("extensions.refused", raised.exception.code)
        self.assertIn("plpython3u", raised.exception.detail)

    def test_one_refused_name_stops_the_whole_step(self):
        from django.db import connection

        from knight_integration.installer.steps import StepFailed, create_extensions

        if connection.vendor != "postgresql":
            self.skipTest("Extensions are a PostgreSQL concept.")

        # Refused before anything is created, not part-way through: a step that
        # applied the allowed half and then failed would leave the store in a
        # state nobody described.
        with self.assertRaises(StepFailed):
            create_extensions(self._context(["citext", "dblink"]))

        with connection.cursor() as cursor:
            cursor.execute("SELECT count(*) FROM pg_extension WHERE extname = 'citext'")
            created = cursor.fetchone()[0]

        self.assertEqual(0, created, "citext was created despite the job being refused.")

    def test_the_step_records_nothing_to_roll_back(self):
        from django.db import connection

        from knight_integration.installer.steps import create_extensions

        if connection.vendor != "postgresql":
            self.skipTest("Extensions are a PostgreSQL concept.")

        context = self._context(["unaccent"])
        create_extensions(context)

        # The decision itself, as a test. A rollback walks `applied` backwards;
        # an extension must never be on that list, because another Feature may
        # have started using it in the meantime (docs/adr/0031).
        self.assertEqual([], context.applied)

    def test_no_rollback_path_can_undo_it(self):
        from knight_integration.installer.runner import _ROLLBACK_FOR

        self.assertNotIn("create-extensions", _ROLLBACK_FOR)


class LocalInstallRuntimeTests(SimpleTestCase):
    """
    `knight_install_local` and Features built for another runtime.

    It matters here and not only in the job path: this command bypasses
    `preflight`, which is where a delivered package gets its runtime checked. CI
    globs every Feature in the repository into it, and since adr/0032 one of them
    is a node package - so without a check a node Feature would land in a Django
    store's INSTALLED_APPS and the store would fail to start.

    Found by CI rather than by reading, on the commit that added the node
    conformance Feature.
    """

    def test_a_feature_for_another_runtime_is_skipped_by_name(self):
        from io import StringIO

        from django.core.management import call_command

        root = Path(__file__).resolve().parents[4] / "features"
        node = root / "knight-feature-node-conformance"

        if not node.is_dir():  # pragma: no cover - the Feature is in the repo
            self.skipTest("The node conformance Feature is not present.")

        with tempfile.TemporaryDirectory() as registry:
            out = StringIO()

            with mock.patch.dict(os.environ, {"KNIGHT_FEATURE_ROOT": registry}):
                call_command("knight_install_local", str(node), stdout=out)

            written = Path(registry) / "installed.json"
            registered = (
                json.loads(written.read_text(encoding="utf-8"))["features"]
                if written.exists()
                else {}
            )

        # Skipped rather than refused: the caller is usually a glob, and stopping
        # on the first foreign Feature would mean installing none of the others.
        self.assertIn("node Feature and this store runs django", out.getvalue())

        # Nothing registered, and the registry is not even written - there was
        # nothing to record.
        self.assertEqual({}, registered)


class FallbackManifestReaderTests(SimpleTestCase):
    """
    The manifest reader `knight_install_local` uses when PyYAML is absent.

    It is the least-exercised code in this package and has now been wrong three
    times, each time in the same shape: it dropped something it could not parse
    and nothing noticed, because PyYAML is installed in development and in CI and
    this path only runs on a bare store.

    Phase 13 found it flattening `django.urls.include`, so a Feature declaring
    routes was registered without them. Phase 16 found it returning an empty
    mapping for every `workers:` block in this repository, so a Feature declaring
    scheduled jobs was registered with none — the same failure one field along —
    and then found it again the moment `restaurant-operations` wrote
    `extensions: []`, which came back as the truthy two-character string `"[]"`.

    Three of the four were found by this comparison rather than by a store
    breaking, which is the argument for it.

    So the tests here are differential: parse a real manifest both ways and
    require the same document. A reader that quietly disagrees with PyYAML is the
    whole bug, and only a comparison catches it.
    """

    @staticmethod
    def _manifests():
        root = Path(__file__).resolve().parents[4] / "features"
        return sorted(root.glob("knight-feature-*/knight_manifest.yaml"))

    def _both(self, path: Path):
        import yaml

        from knight_integration.management.commands.knight_install_local import _read_simple_yaml

        text = path.read_text(encoding="utf-8")
        fallback = _read_simple_yaml(text)
        reference = yaml.safe_load(text)

        # The one field it deliberately does not read: a folded scalar, needed by
        # nobody, and skipped rather than half-joined.
        fallback.pop("description", None)
        reference.pop("description", None)

        return fallback, reference

    def test_every_manifest_in_this_repository_reads_the_same_as_pyyaml(self):
        paths = self._manifests()
        self.assertNotEqual([], paths, "No manifests found; this test would pass having checked nothing.")

        for path in paths:
            with self.subTest(manifest=path.parent.name):
                fallback, reference = self._both(path)
                self.assertEqual(reference, fallback)

    def test_an_inline_sequence_is_a_list_and_not_a_string(self):
        # The fourth of these. `"[]"` is truthy and iterable, so a caller asking
        # "does this Feature declare extensions" got yes, and one asking which
        # ones got two characters.
        from knight_integration.management.commands.knight_install_local import _read_simple_yaml

        document = _read_simple_yaml(
            "migrations:\n  extensions: []\n  others: [pg_trgm, postgis]\n"
        )

        self.assertEqual([], document["migrations"]["extensions"])
        self.assertEqual(["pg_trgm", "postgis"], document["migrations"]["others"])

    def test_a_feature_that_declares_workers_gets_them(self):
        # Named separately from the comparison above because this is the
        # regression: `workers` came back as `{}` for every Feature that has one.
        from knight_integration.management.commands.knight_install_local import _read_simple_yaml

        document = _read_simple_yaml(
            "slug: loyalty-rewards\n"
            "workers:\n"
            "  - name: expire-points\n"
            "    entrypoint: knight_feature_loyalty_rewards.services.expire_stale\n"
            "    schedule: daily\n"
        )

        self.assertEqual(
            [{
                "name": "expire-points",
                "entrypoint": "knight_feature_loyalty_rewards.services.expire_stale",
                "schedule": "daily",
            }],
            document["workers"],
        )

    def test_a_version_range_is_not_torn_in_half_at_its_comma(self):
        # The packaging tool had this bug and it was fixed there in phase 15;
        # this copy of the code had it too and nothing had read the field
        # (docs/phase-15-verification.md).
        from knight_integration.management.commands.knight_install_local import _read_simple_yaml

        document = _read_simple_yaml(
            'dependencies:\n  features:\n    - { slug: analytics-core, version: ">=1.0.0,<2.0.0" }\n'
        )

        self.assertEqual(
            [{"slug": "analytics-core", "version": ">=1.0.0,<2.0.0"}],
            document["dependencies"]["features"],
        )

    def test_a_scalar_sequence_is_read_rather_than_skipped(self):
        from knight_integration.management.commands.knight_install_local import _read_simple_yaml

        document = _read_simple_yaml("migrations:\n  required: true\n  extensions:\n    - pg_trgm\n")

        self.assertEqual({"required": True, "extensions": ["pg_trgm"]}, document["migrations"])

    def test_booleans_are_booleans_rather_than_the_string_true(self):
        from knight_integration.management.commands.knight_install_local import _read_simple_yaml

        # `"false"` is truthy, and `migrations.reversible` is the field where
        # that would decide whether a rollback is attempted.
        document = _read_simple_yaml("migrations:\n  required: true\n  reversible: false\n")

        self.assertIs(True, document["migrations"]["required"])
        self.assertIs(False, document["migrations"]["reversible"])

    def test_a_shape_it_cannot_read_raises_rather_than_dropping_it(self):
        from knight_integration.management.commands.knight_install_local import (
            ManifestUnreadable,
            _read_simple_yaml,
        )

        # The lesson of the three bugs above, as a rule: silence is what let each
        # of them survive. A shape this reader does not understand is refused,
        # and the command tells the operator to install PyYAML.
        with self.assertRaises(ManifestUnreadable):
            _read_simple_yaml("workers:\n  - name:\n      nested: deeper\n")
