"""
Features that are services rather than packages.

The store runs none of their code, so almost everything worth asserting here is
about what it refuses to do on their behalf: forward an event it does not
publish, hang a screen in a slot that does not exist, proxy a method the
manifest did not declare, or pass a shopper's credentials to somebody else's
server (``docs/adr/0033-api-driven-features.md``).

The two architectures share the same step vocabulary on purpose, so these tests
also pin the thing that makes the pivot safe: an external job names no verb the
in-process pipeline does not already have.
"""

from __future__ import annotations

import base64
import hashlib
import json
import shutil
import tempfile
from pathlib import Path
from unittest import mock

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from django.test import SimpleTestCase

from knight_integration.external import bus
from knight_integration.external.catalogue import KNOWN_EVENTS, UI_SLOTS
from knight_integration.external.contract import ExternalContract, contract_of, external_features
from knight_integration.external.signing import canonical_string, sign, verify
from knight_integration.installer.runner import JobRunner
from knight_integration.installer.state import InstalledFeature


def _key_pair() -> tuple[ec.EllipticCurvePrivateKey, str]:
    private = ec.generate_private_key(ec.SECP256R1())
    public_der = private.public_key().public_bytes(
        encoding=serialization.Encoding.DER,
        format=serialization.PublicFormat.SubjectPublicKeyInfo,
    )
    return private, base64.b64encode(public_der).decode("ascii")


def _serve(directory: Path, test) -> str:
    """
    A one-request-at-a-time HTTP server over `directory`, for the length of one test.

    Real HTTP because `fetch` is one of the steps being exercised: a test that
    handed the runner a local path would prove the store can read a file, which
    is not the thing in question.
    """
    import functools
    import http.server
    import threading

    handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(directory))
    handler.log_message = lambda *args, **kwargs: None  # type: ignore[assignment]

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()

    def stop():
        server.shutdown()
        server.server_close()

    test.addCleanup(stop)

    return f"http://127.0.0.1:{server.server_address[1]}"


class _StubClient:
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
            "installedVersion": installed_version,
        }


class ExternalDeliveryTests(SimpleTestCase):
    """Taking delivery of a configuration document instead of an archive."""

    #: KNIGHT's external install pipeline, verbatim. Every verb here is one the
    #: in-process pipeline already had, which is the whole reason this pivot
    #: does not break a store that has not been redeployed.
    PIPELINE = ["preflight", "fetch", "verify", "backup", "configure", "install", "enable", "healthcheck"]

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="knight-external-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

        # Served over real HTTP rather than handed to the runner as a path.
        # `fetch` is the step under test as much as `install` is, and a test that
        # mocked the download would not have caught a store that could not read
        # what KNIGHT serves.
        self.served = Path(tempfile.mkdtemp(prefix="knight-artifacts-"))
        self.addCleanup(shutil.rmtree, self.served, ignore_errors=True)
        self.base_url = _serve(self.served, self)

        self.private, self.public = _key_pair()

    def _config(self):
        from knight_integration.conf import get_settings

        config = get_settings()
        object.__setattr__(config, "feature_root", str(self.root))
        object.__setattr__(config, "signing_keys", {"dev": self.public})
        return config

    def _document(self, **overrides) -> dict:
        document = {
            "apiVersion": "knight.dev/v1",
            "architecture": "external_service",
            "slug": "subscriptions",
            "version": "2.0.0",
            "name": "Subscriptions",
            "service": {
                "base_url": "https://subscriptions.knight.dev",
                "auth": "hmac-sha256",
                "health": "/healthz",
                "secret": "SUBSCRIPTIONS_SERVICE_SECRET",
            },
            "webhooks": [
                {"event": "order.placed", "path": "/hooks/order-placed", "delivery": "at-least-once"},
                {"event": "order.cancelled", "path": "/hooks/order-cancelled", "delivery": "at-least-once"},
            ],
            "api_proxies": [
                {"prefix": "subscriptions/", "upstream": "/api/v1/", "methods": ["GET", "POST"], "identity": "customer"},
            ],
            "ui_mounts": [
                {"slot": "admin.sidebar", "label": "Subscriptions", "path": "/admin", "kind": "iframe"},
            ],
        }
        document.update(overrides)
        return document

    def _job(self, document: dict, steps: list[str] | None = None, job_type: str = "Install") -> tuple[dict, Path]:
        artifact = self.served / f"{document['slug']}-{document['version']}.json"
        artifact.write_bytes(json.dumps(document, sort_keys=True, separators=(",", ":")).encode("utf-8"))

        raw = artifact.read_bytes()
        digest = hashlib.sha256(raw).hexdigest()
        signature = base64.b64encode(
            self.private.sign(digest.encode("ascii"), ec.ECDSA(hashes.SHA256()))
        ).decode("ascii")

        job = {
            "jobId": "job-1",
            "type": job_type,
            "featureSlug": document["slug"],
            "targetVersion": document["version"],
            # The field that tells the agent what the bytes it is about to fetch
            # *are*. It has to know before it fetches: the two architectures want
            # the same bytes handled completely differently.
            "architecture": "external_service",
            "steps": steps if steps is not None else self.PIPELINE,
            "artifact": {
                "packageReference": "subscriptions-2.0.0.json",
                "digest": digest,
                "sizeBytes": len(raw),
                "signature": signature,
                "signingKeyId": "dev",
                "downloadUrl": f"{self.base_url}/{artifact.name}",
            },
            "configuration": {"version": 3, "valuesJson": json.dumps({"plan": "monthly"})},
        }
        return job, artifact

    def _run(self, job: dict) -> tuple[_StubClient, object]:
        client = _StubClient(job=job)
        runner = JobRunner(client=client, config=self._config())
        outcome = runner.execute(job)
        return client, outcome

    def test_it_registers_webhooks_routes_and_screens_without_touching_the_database(self):
        job, _ = self._job(self._document())
        client, outcome = self._run(job)

        self.assertTrue(outcome.succeeded, client.completion)

        # No migration was named and none ran. The single most important
        # consequence of the whole architecture: this Feature has no schema in
        # this store, so there is nothing to migrate and nothing to reverse.
        self.assertNotIn("migrate", [step for step, _, _ in client.steps])

        from knight_integration.installer.state import get_registry

        installed = get_registry(self.root).get("subscriptions")
        self.assertIsNotNone(installed)

        contract = contract_of(installed)
        self.assertIsNotNone(contract)
        self.assertEqual(2, len(contract.webhooks))
        self.assertEqual(1, len(contract.api_proxies))
        self.assertEqual(1, len(contract.ui_mounts))
        self.assertEqual("https://subscriptions.knight.dev", contract.base_url)

    def test_no_package_directory_is_created(self):
        job, _ = self._job(self._document())
        self._run(job)

        # There is no code. A directory here would be a store that had made
        # somewhere for a package that does not exist, and the next reader would
        # reasonably wonder what was meant to be in it.
        self.assertFalse((self.root / "subscriptions").exists())

    def test_the_registry_entry_names_no_module_to_import(self):
        job, _ = self._job(self._document())
        self._run(job)

        from knight_integration.installer.state import get_registry

        installed = get_registry(self.root).get("subscriptions")

        # Plausible-looking values here would have the store's own loader try to
        # import a package that was never delivered, and lose the feature list
        # for every other Feature while it did.
        self.assertEqual("", installed.installed_app)
        self.assertIsNone(installed.url_include)

    def test_it_refuses_an_event_this_store_does_not_publish(self):
        document = self._document(webhooks=[{"event": "order.plaecd", "path": "/hooks/typo"}])
        job, _ = self._job(document)
        client, outcome = self._run(job)

        # Without this the Feature installs cleanly, passes its health check and
        # never hears anything — and the person who notices is the merchant,
        # weeks later. KNIGHT cannot make this check: it does not know what any
        # particular store publishes.
        self.assertFalse(outcome.succeeded)
        self.assertEqual("install.unknown_event", client.completion["failureCode"])

    def test_it_refuses_a_slot_this_store_does_not_offer(self):
        document = self._document(ui_mounts=[{"slot": "admin.nowhere", "label": "X", "path": "/x"}])
        job, _ = self._job(document)
        client, outcome = self._run(job)

        self.assertFalse(outcome.succeeded)
        self.assertEqual("install.unknown_slot", client.completion["failureCode"])

    def test_it_refuses_a_document_that_disagrees_with_the_job(self):
        document = self._document(architecture="in_process")
        job, _ = self._job(document)
        client, outcome = self._run(job)

        # The job says one thing and the signed document says another. Acting on
        # either would be choosing which of two disagreeing sources to trust,
        # and the honest answer is neither.
        self.assertFalse(outcome.succeeded)
        self.assertEqual("install.wrong_architecture", client.completion["failureCode"])

    def test_a_tampered_configuration_never_reaches_the_registry(self):
        job, artifact = self._job(self._document())
        artifact.write_bytes(artifact.read_bytes().replace(b"subscriptions.knight.dev", b"attacker.example.com"))

        client, outcome = self._run(job)

        # The reason the configuration is signed at all. Without the digest
        # check the store would wire a proxy route — carrying its customers'
        # requests — to whatever host answered the download URL.
        self.assertFalse(outcome.succeeded)
        self.assertEqual("digest.mismatch", client.completion["failureCode"])

        from knight_integration.installer.state import get_registry

        self.assertIsNone(get_registry(self.root).get("subscriptions"))

    def test_disable_stops_it_serving_and_keeps_the_registration(self):
        job, _ = self._job(self._document())
        self._run(job)

        disable, _ = self._job(self._document(), steps=["disable"], job_type="Disable")
        self._run(disable)

        from knight_integration.installer.state import get_registry

        installed = get_registry(self.root).get("subscriptions")

        # Disable is not uninstall. The registration stays so that re-entitling
        # the customer next week does not need the whole delivery again.
        self.assertIsNotNone(installed)
        self.assertFalse(installed.enabled)

        # And nothing disabled is forwarded anything.
        self.assertEqual([], external_features(self.root))

    def test_uninstall_unregisters_it(self):
        job, _ = self._job(self._document())
        self._run(job)

        uninstall, _ = self._job(
            self._document(), steps=["disable", "backup", "remove-package"], job_type="Uninstall"
        )
        self._run(uninstall)

        from knight_integration.installer.state import get_registry

        self.assertIsNone(get_registry(self.root).get("subscriptions"))

    def test_a_rollback_restores_the_registration_the_backup_kept(self):
        first, _ = self._job(self._document())
        self._run(first)

        second, _ = self._job(self._document(version="2.1.0", webhooks=[{"event": "order.paid", "path": "/hooks/paid"}]))
        self._run(second)

        from knight_integration.installer.state import get_registry

        self.assertEqual("2.1.0", get_registry(self.root).get("subscriptions").version)

        rollback, _ = self._job(
            self._document(), steps=["restore-package", "configure", "enable", "healthcheck"], job_type="Rollback"
        )
        client, outcome = self._run(rollback)

        self.assertTrue(outcome.succeeded, client.completion)

        restored = get_registry(self.root).get("subscriptions")

        # Restored from the local copy `backup` kept, not fetched: a rollback
        # job names the version it is rolling *to* and carries the artifact of
        # the one it is rolling *from*, so a store that fetched here would
        # reinstall the version it was trying to leave.
        self.assertEqual("2.0.0", restored.version)
        self.assertTrue(restored.enabled)
        self.assertEqual(2, len(contract_of(restored).webhooks))

    def test_a_rollback_with_nothing_kept_fails_rather_than_reporting_success(self):
        rollback, _ = self._job(
            self._document(), steps=["restore-package"], job_type="Rollback"
        )
        client, outcome = self._run(rollback)

        # An operator told the store is back on the old version stops looking.
        self.assertFalse(outcome.succeeded)
        self.assertEqual("rollback.no_backup", client.completion["failureCode"])


class EventBusTests(SimpleTestCase):
    """Which Features hear about what."""

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="knight-bus-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

    def _register(self, slug: str, events: list[str], *, enabled: bool = True) -> None:
        from knight_integration.installer.state import get_registry

        get_registry(self.root).record(
            InstalledFeature(
                slug=slug,
                version="1.0.0",
                app_label=slug.replace("-", "_"),
                installed_app="",
                digest="d" * 64,
                installed_at="2026-08-28T00:00:00+00:00",
                enabled=enabled,
                extra={
                    "architecture": "external_service",
                    "service": {"base_url": "https://example.test"},
                    "webhooks": [{"event": event, "path": f"/hooks/{event}"} for event in events],
                    "api_proxies": [],
                    "ui_mounts": [],
                },
            )
        )

    def test_only_features_that_subscribed_are_told(self):
        self._register("subscriptions", ["order.placed"])
        self._register("marketplaces", ["order.cancelled"])

        delivered = []
        sent = bus.publish(
            "order.placed",
            {"id": 1},
            feature_root=self.root,
            deliver=lambda contract, subscription, payload: delivered.append(contract.slug),
        )

        self.assertEqual(1, sent)
        self.assertEqual(["subscriptions"], delivered)

    def test_a_disabled_feature_is_told_nothing(self):
        self._register("subscriptions", ["order.placed"], enabled=False)

        delivered = []
        sent = bus.publish(
            "order.placed", {"id": 1}, feature_root=self.root,
            deliver=lambda contract, subscription, payload: delivered.append(contract.slug),
        )

        # An entitlement that lapsed is a commercial fact and the store enforces
        # it now, not at the next restart.
        self.assertEqual(0, sent)
        self.assertEqual([], delivered)

    def test_publishing_an_event_the_store_never_declared_delivers_nothing(self):
        self._register("subscriptions", ["order.placed"])

        # The store's own code publishing something not in KNOWN_EVENTS. No
        # Feature could have subscribed to it, so nothing can hear it, and the
        # author almost certainly meant a name that is on the list.
        self.assertEqual(0, bus.publish("order.invented", {}, feature_root=self.root))

    def test_one_features_delivery_failing_does_not_stop_the_next(self):
        self._register("first", ["order.placed"])
        self._register("second", ["order.placed"])

        seen = []

        def deliver(contract, subscription, payload):
            seen.append(contract.slug)
            if contract.slug == "first":
                raise RuntimeError("their server is down")

        sent = bus.publish("order.placed", {}, feature_root=self.root, deliver=deliver)

        # The store's own transaction is long finished; this is fan-out, and one
        # subscriber's bad afternoon is not the others' problem.
        self.assertEqual(2, len(seen))
        self.assertEqual(1, sent)

    def test_every_event_a_catalogue_feature_could_want_is_declared(self):
        # The catalogue is the store's half of the contract. This is a
        # smoke test that it is not empty and is spelled the way the manifest
        # validator requires.
        self.assertIn("order.placed", KNOWN_EVENTS)
        self.assertIn("admin.sidebar", UI_SLOTS)
        self.assertTrue(all("." in name for name in KNOWN_EVENTS))


class RequestSigningTests(SimpleTestCase):
    """Proving to a Feature's service that a request came from this store."""

    def test_a_signature_covers_the_body(self):
        headers = sign("s3cret", "POST", "/orders", b'{"total":100}')

        # A proxy in the middle changing the total must break the signature.
        self.assertFalse(verify("s3cret", headers, "POST", "/orders", b'{"total":1}'))
        self.assertTrue(verify("s3cret", headers, "POST", "/orders", b'{"total":100}'))

    def test_a_signature_covers_the_path_and_method(self):
        headers = sign("s3cret", "GET", "/orders", b"")

        self.assertFalse(verify("s3cret", headers, "DELETE", "/orders", b""))
        self.assertFalse(verify("s3cret", headers, "GET", "/customers", b""))

    def test_another_secret_does_not_verify(self):
        headers = sign("s3cret", "GET", "/orders", b"")

        self.assertFalse(verify("someone-elses", headers, "GET", "/orders", b""))

    def test_a_request_with_no_signature_is_refused(self):
        self.assertFalse(verify("s3cret", {}, "GET", "/orders", b""))

    def test_a_stale_timestamp_is_refused(self):
        headers = sign("s3cret", "GET", "/orders", b"")
        headers["X-Knight-Timestamp"] = "1"

        # The whole point of covering a timestamp is that it expires. A captured
        # request has to stop working.
        self.assertFalse(verify("s3cret", headers, "GET", "/orders", b""))

    def test_the_canonical_string_is_the_same_on_both_sides(self):
        # Both ends derive it independently. One side sending the string it
        # signed would be asking the other to agree with itself.
        first = canonical_string("POST", "/x", "1", "n", b"body")
        second = canonical_string("post", "/x", "1", "n", b"body")

        self.assertEqual(first, second)


class SharedSecretTests(SimpleTestCase):
    """
    Where the secret this store signs with comes from.

    KNIGHT issues it per (store, feature) and rotates it, and a rotation reaches
    the store as a configuration version written beside the registry
    (`docs/adr/0034-a-shared-secret-has-a-lifetime.md`). What is asserted here is
    the precedence, because getting it the wrong way round fails silently: a
    store pinned to an environment variable ignores every rotation and looks
    perfectly healthy until the overlap window closes.
    """

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="knight-secrets-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

        self.contract = ExternalContract(
            slug="subscriptions",
            version="2.1.0",
            base_url="https://subscriptions.knight.dev",
            auth="hmac-sha256",
            health_path="/healthz",
            secret_name="SUBSCRIPTIONS_SERVICE_SECRET",
            webhooks=[],
            api_proxies=[],
            ui_mounts=[],
        )

    def deliver(self, secret: str, *, version: int = 4) -> None:
        """The file the installer writes when KNIGHT sends a configuration."""
        (self.root / "subscriptions.config.json").write_text(
            json.dumps(
                {"version": version, "values": {}, "secrets": {"SUBSCRIPTIONS_SERVICE_SECRET": secret}}
            ),
            encoding="utf-8",
        )

    def secret(self, **environment) -> str:
        from knight_integration.external.signing import secret_for

        with mock.patch("knight_integration.conf.get_settings") as settings:
            settings.return_value.feature_root = str(self.root)

            with mock.patch.dict("os.environ", environment, clear=True):
                return secret_for(self.contract, required=False)

    def test_what_knight_delivered_wins_over_the_environment(self):
        self.deliver("the-secret-knight-issued-and-has-since-rotated")

        # The assertion this class exists for. An environment variable that took
        # precedence would mean a store quietly ignoring every rotation.
        self.assertEqual(
            "the-secret-knight-issued-and-has-since-rotated",
            self.secret(SUBSCRIPTIONS_SERVICE_SECRET="what-an-operator-typed-on-day-one"),
        )

    def test_the_environment_is_used_when_knight_has_delivered_nothing(self):
        # A developer against a service on their laptop, and an operator while a
        # store is being brought up.
        self.assertEqual(
            "a-local-secret", self.secret(SUBSCRIPTIONS_SERVICE_SECRET="a-local-secret")
        )

    def test_a_rotation_is_picked_up_without_a_restart(self):
        self.deliver("the-first-secret")
        self.assertEqual("the-first-secret", self.secret())

        self.deliver("the-second-secret", version=5)

        # Read from the file every time. A value cached at import would keep a
        # store signing with a secret whose window is closing, which is the one
        # failure this arrangement exists to avoid.
        self.assertEqual("the-second-secret", self.secret())

    def test_an_unreadable_configuration_is_not_a_broken_store(self):
        (self.root / "subscriptions.config.json").write_text("{ this is not json", encoding="utf-8")

        self.assertEqual(
            "a-local-secret", self.secret(SUBSCRIPTIONS_SERVICE_SECRET="a-local-secret")
        )

    def test_a_feature_with_no_secret_anywhere_raises_rather_than_signing_with_nothing(self):
        from knight_integration.external.signing import secret_for

        with mock.patch("knight_integration.conf.get_settings") as settings:
            settings.return_value.feature_root = str(self.root)

            with mock.patch.dict("os.environ", {}, clear=True):
                with self.assertRaises(LookupError):
                    secret_for(self.contract)


class ProxyTests(SimpleTestCase):
    """What the store will and will not forward."""

    def setUp(self) -> None:
        self.contract = ExternalContract(
            slug="subscriptions",
            version="2.0.0",
            base_url="https://subscriptions.knight.dev",
            auth="hmac-sha256",
            health_path="/healthz",
            secret_name="SUBSCRIPTIONS_SERVICE_SECRET",
            webhooks=[],
            api_proxies=[],
            ui_mounts=[],
        )

    def _request(self, method="GET", user=None, body=b""):
        from django.test import RequestFactory

        request = getattr(RequestFactory(), method.lower())("/subscriptions/plans", data=body, content_type="application/json")
        request.user = user
        return request

    def test_a_method_the_manifest_did_not_declare_is_refused_by_the_store(self):
        from knight_integration.external import proxy

        response = proxy.forward(self._request("DELETE"), self.contract, "plans", "/api/v1/", ["GET"], "anonymous")

        # The store's own 405, which never reaches the service. A route that
        # acquired a DELETE because nobody wrote a method list is a read-only
        # Feature that can now delete things.
        self.assertEqual(405, response.status_code)

    def test_an_anonymous_caller_is_refused_on_a_customer_route(self):
        from django.contrib.auth.models import AnonymousUser

        from knight_integration.external import proxy

        response = proxy.forward(
            self._request("GET", user=AnonymousUser()), self.contract, "plans", "/api/v1/", ["GET"], "customer"
        )

        self.assertEqual(403, response.status_code)

    def test_the_shoppers_credentials_are_never_forwarded(self):
        from knight_integration.external import proxy

        request = self._request("GET")
        request.META["HTTP_COOKIE"] = "sessionid=secret"
        request.META["HTTP_AUTHORIZATION"] = "Bearer shopper-token"

        captured = {}

        def fake_request(method, url, **kwargs):
            captured.update(kwargs.get("headers") or {})
            raise ImportError("stop here; the headers are what is being asserted")

        with mock.patch.dict("os.environ", {"SUBSCRIPTIONS_SERVICE_SECRET": "s3cret"}):
            with mock.patch("requests.request", side_effect=fake_request):
                try:
                    proxy.forward(request, self.contract, "plans", "/api/v1/", ["GET"], "anonymous")
                except ImportError:
                    pass

        # The single most important assertion in this file. A Feature's service
        # holding a credential it could replay against the store is exactly what
        # proxying instead of mounting is supposed to prevent.
        lowered = {name.lower() for name in captured}
        self.assertNotIn("cookie", lowered)
        self.assertNotIn("authorization", lowered)
        self.assertIn("X-Knight-Signature", captured)

    def test_a_feature_with_no_secret_configured_is_not_called_unsigned(self):
        from knight_integration.external import proxy

        with mock.patch.dict("os.environ", {}, clear=True):
            response = proxy.forward(self._request("GET"), self.contract, "plans", "/api/v1/", ["GET"], "anonymous")

        # An unsigned request is not a fallback. A service that accepted one
        # would accept anybody's.
        self.assertEqual(503, response.status_code)
