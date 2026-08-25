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
import shutil
import tempfile
import zipfile
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from django.test import SimpleTestCase

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
            "preflight", "fetch", "verify", "backup", "install", "migrate",
            "configure", "enable", "reload", "healthcheck", "disable",
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
