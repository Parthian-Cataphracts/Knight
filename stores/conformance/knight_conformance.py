"""
Conformance checker for the KNIGHT store-integration contract.

A customer store may be written in anything. What makes it a store is not its
framework but the handful of HTTP calls in this file, so this is the definition
a new integration is finished against: run it until it is green.

    python knight_conformance.py selftest
    python knight_conformance.py check --knight-url ... --client-id ... --client-secret ...

`selftest` needs nothing running. It reproduces the two signed strings from
docs/contracts/store-integration.samples.json, which is the byte-for-byte
authority both KNIGHT and the reference store are already tested against - so a
checker that has drifted from the contract fails before it can report anything
misleading about somebody else's store.

`check` needs a KNIGHT and a store. It performs a real handshake with real
credentials, so it is a test against a deployment rather than a mock, and every
assertion it makes is one an operator would otherwise make by hand.

Standard library only, deliberately: this is the first thing a team wiring a new
store runs, and asking them to solve a dependency problem first is a poor way to
begin.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import secrets
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
SAMPLES = REPO_ROOT / "docs" / "contracts" / "store-integration.samples.json"

SIGNATURE_VERSION = "1"

#: The path KNIGHT polls for health. Configurable on the KNIGHT side
#: (Stores:Probe:HealthPath); this is the default every store should serve.
HEALTH_PATH = "/api/knight/health"

#: Published unauthenticated, and compared exact after trimming.
DOMAIN_VERIFICATION_PATH = "/.well-known/knight-domain-verification"

REQUIRED_HANDSHAKE_FIELDS = (
    "storeId",
    "storeName",
    "slug",
    "environment",
    "integrationStatus",
    "accessToken",
    "tokenType",
    "expiresIn",
    "expiresAt",
    "entitlementSigningKey",
    "domainVerificationOutstanding",
    "heartbeatSeconds",
    "featureRefreshSeconds",
)

REQUIRED_HEALTH_FIELDS = ("status", "checkedAt", "version", "environment", "dependencies")

HEALTH_STATUSES = ("healthy", "degraded", "unhealthy")


# --- The two signed strings ---------------------------------------------------


def unix(value: Any) -> int:
    """
    Unix seconds from an ISO-8601 timestamp.

    Both canonical forms carry integers rather than formatted dates, because two
    languages agree on an integer and argue about everything else.
    """
    if value is None:
        return int(time.time())
    if isinstance(value, (int, float)):
        return int(value)

    text = str(value).replace("Z", "+00:00")
    return int(datetime.fromisoformat(text).timestamp())


def entitlement_canonical_form(payload: dict[str, Any]) -> str:
    """
    The string KNIGHT signs an entitlement set with.

    Features are sorted by slug with an ordinal comparison so the order can never
    depend on a database collation or the culture the process runs in, and an
    absent expiry is a single hyphen so it cannot be confused with a field that
    was left out.
    """
    features = sorted(payload.get("features", []), key=lambda feature: feature["slug"])
    rendered = ",".join(
        f"{feature['slug']}:{unix(feature['expiresAt']) if feature.get('expiresAt') else '-'}"
        for feature in features
    )

    return "|".join(
        [
            "knight-entitlements",
            SIGNATURE_VERSION,
            str(payload.get("storeId", "")),
            str(payload.get("customerId", "")),
            str(payload.get("environment", "")),
            str(unix(payload.get("issuedAt"))),
            str(unix(payload.get("staleAfter"))),
            rendered,
        ]
    )


def request_canonical_form(method: str, path: str, timestamp: str, nonce: str) -> str:
    """
    The string KNIGHT signs a request to a store with.

    The path only, never the host: a proxy in front of a store may rewrite the
    host, and binding the signature to it would break every store behind one.
    """
    return f"knight-request|{SIGNATURE_VERSION}|{method.upper()}|{path}|{timestamp}|{nonce}"


def sign(key_base64: str, canonical: str) -> str:
    """Base64 HMAC-SHA256, the one primitive both signed strings use."""
    digest = hmac.new(base64.b64decode(key_base64), canonical.encode("utf-8"), hashlib.sha256).digest()
    return base64.b64encode(digest).decode("ascii")


# --- Reporting ----------------------------------------------------------------


class Report:
    """
    Accumulates results so the run reports everything it found rather than
    stopping at the first problem. A team wiring a store wants the whole list.
    """

    GREEN = "\033[0;32m"
    RED = "\033[0;31m"
    YELLOW = "\033[1;33m"
    DIM = "\033[2m"
    OFF = "\033[0m"

    def __init__(self) -> None:
        self.failures = 0
        self.warnings = 0

    def ok(self, description: str, detail: str = "") -> None:
        print(f"  {self.GREEN}PASS{self.OFF} {description}" + (f"  {self.DIM}{detail}{self.OFF}" if detail else ""))

    def fail(self, description: str, detail: str = "") -> None:
        self.failures += 1
        print(f"  {self.RED}FAIL{self.OFF} {description}" + (f"\n       {detail}" if detail else ""))

    def warn(self, description: str, detail: str = "") -> None:
        self.warnings += 1
        print(f"  {self.YELLOW}WARN{self.OFF} {description}" + (f"\n       {detail}" if detail else ""))

    def check(self, description: str, condition: bool, detail: str = "") -> bool:
        if condition:
            self.ok(description)
        else:
            self.fail(description, detail)
        return condition

    def section(self, title: str) -> None:
        print(f"\n\033[1m{title}\033[0m")

    def summary(self) -> int:
        print("")
        if self.failures:
            print(f"  {self.RED}{self.failures} failure(s){self.OFF}"
                  + (f", {self.warnings} warning(s)" if self.warnings else ""))
            return 1
        if self.warnings:
            print(f"  {self.GREEN}No failures{self.OFF}, {self.warnings} warning(s).")
            return 0
        print(f"  {self.GREEN}Everything the contract requires is in place.{self.OFF}")
        return 0


# --- HTTP ---------------------------------------------------------------------


class Response:
    def __init__(self, status: int, body: bytes, headers: dict[str, str]) -> None:
        self.status = status
        self.body = body
        self.headers = headers

    def json(self) -> Any:
        return json.loads(self.body.decode("utf-8"))

    @property
    def text(self) -> str:
        return self.body.decode("utf-8", errors="replace").strip()


def http(
    method: str,
    url: str,
    body: Any = None,
    headers: dict[str, str] | None = None,
    timeout: int = 15,
) -> Response:
    """
    One request, returning the response whatever its status.

    A 401 is an answer here, not an exception: several of the checks below exist
    precisely to assert that something is refused.
    """
    data = json.dumps(body).encode("utf-8") if body is not None else None
    request = urllib.request.Request(url, data=data, method=method.upper())
    request.add_header("Accept", "application/json")
    if data is not None:
        request.add_header("Content-Type", "application/json")
    for name, value in (headers or {}).items():
        request.add_header(name, value)

    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return Response(response.status, response.read(), dict(response.headers))
    except urllib.error.HTTPError as error:
        return Response(error.code, error.read(), dict(error.headers))
    except Exception as error:  # noqa: BLE001 - a DNS, TLS or refused connection
        # The commonest state of a store that is being wired up for the first
        # time. Reported as a finding rather than a traceback, because a stack
        # trace here tells the reader nothing they did not already suspect.
        return Response(0, f"{type(error).__name__}: {error}".encode("utf-8"), {})


# --- selftest -----------------------------------------------------------------


def selftest() -> int:
    """
    Proves this checker still produces the contract's own signed strings.

    Without this, a checker that had drifted would report confident, wrong
    verdicts about somebody else's store - which is worse than not checking.
    """
    report = Report()
    report.section("Canonical forms, against docs/contracts/store-integration.samples.json")

    if not SAMPLES.is_file():
        report.fail("the samples file is readable", f"not found at {SAMPLES}")
        return report.summary()

    samples = json.loads(SAMPLES.read_text(encoding="utf-8"))

    entitlements = samples["entitlementCanonicalForm"]
    produced = entitlement_canonical_form(entitlements["payload"])
    report.check(
        "the entitlement canonical form matches",
        produced == entitlements["expected"],
        f"expected: {entitlements['expected']}\n       produced: {produced}",
    )

    requests = samples["requestCanonicalForm"]
    payload = requests["payload"]
    produced = request_canonical_form(
        payload["method"], payload["path"], payload["timestamp"], payload["nonce"]
    )
    report.check(
        "the request canonical form matches",
        produced == requests["expected"],
        f"expected: {requests['expected']}\n       produced: {produced}",
    )

    # A key and a string of known bytes, so a change to the primitive - the hash,
    # the encoding, the order of arguments - fails here rather than against a
    # live store where it would look like the store's fault.
    key = base64.b64encode(b"knight-conformance-selftest-key").decode("ascii")
    report.check(
        "HMAC-SHA256 is base64-encoded as the contract expects",
        sign(key, "knight-request|1|GET|/api/knight/health|1787140800|abc")
        == base64.b64encode(
            hmac.new(
                b"knight-conformance-selftest-key",
                b"knight-request|1|GET|/api/knight/health|1787140800|abc",
                hashlib.sha256,
            ).digest()
        ).decode("ascii"),
    )

    report.section("Timestamp handling")
    report.check("Z and +00:00 are the same instant",
                 unix("2026-08-19T12:00:00Z") == unix("2026-08-19T12:00:00+00:00"))
    report.check("an absent expiry renders as a hyphen, not an empty string",
                 entitlement_canonical_form(
                     {"storeId": "s", "customerId": "c", "environment": "Production",
                      "issuedAt": 0, "staleAfter": 0,
                      "features": [{"slug": "a", "expiresAt": None}]}
                 ).endswith("|a:-"))
    report.check("features are ordered by slug, not by arrival",
                 entitlement_canonical_form(
                     {"storeId": "s", "customerId": "c", "environment": "Production",
                      "issuedAt": 0, "staleAfter": 0,
                      "features": [{"slug": "z", "expiresAt": None}, {"slug": "a", "expiresAt": None}]}
                 ).endswith("|a:-,z:-"))

    return report.summary()


# --- check --------------------------------------------------------------------


def check(
    knight_url: str,
    client_id: str,
    client_secret: str,
    environment: str,
    store_url: str | None,
    store_version: str,
    runtime: str,
) -> int:
    report = Report()
    knight_url = knight_url.rstrip("/")
    ingest = f"{knight_url}/api/v1/ingest"

    # --- Handshake ------------------------------------------------------------

    report.section("Handshake")

    nonce = secrets.token_hex(12)
    handshake_body = {
        "clientId": client_id,
        "clientSecret": client_secret,
        "environment": environment,
        "storeVersion": store_version,
        "runtime": runtime,
        "nonce": nonce,
    }

    response = http("POST", f"{ingest}/handshake", handshake_body)
    if not report.check(
        "KNIGHT accepts the credentials",
        response.status == 200,
        f"HTTP {response.status}: {response.text[:300]}",
    ):
        print("\n  Nothing further can be checked without a session.")
        return report.summary()

    session = response.json()

    missing = [field for field in REQUIRED_HANDSHAKE_FIELDS if field not in session]
    report.check(
        "the handshake response carries every field the contract requires",
        not missing,
        f"missing: {', '.join(missing)}",
    )

    report.check("the token is a Bearer token", session.get("tokenType") == "Bearer")
    report.check(
        "the environment KNIGHT returns is the one that was asked for",
        session.get("environment") == environment,
        f"asked for {environment}, got {session.get('environment')}",
    )

    token = session.get("accessToken", "")
    signing_key = session.get("entitlementSigningKey", "")
    store_id = session.get("storeId", "")
    status = session.get("integrationStatus")

    lifetime = int(session.get("expiresIn") or 0)
    report.check(
        "the store token is short-lived",
        0 < lifetime <= 12 * 3600,
        f"expiresIn is {lifetime}s. A store token cannot be rotated, so it is kept short instead.",
    )

    if status == "Pending":
        report.warn(
            "the store is Pending, not Connected",
            "Its domain has not been proven yet. Publish the verification token and verify it "
            "in the dashboard - until then KNIGHT will not treat this store as linked.",
        )
    elif status == "Connected":
        report.ok("the store is Connected")
    else:
        report.warn(f"the store's integration status is {status}")

    authorised = {"Authorization": f"Bearer {token}"}

    # --- Replay ---------------------------------------------------------------

    report.section("Replay protection")

    replayed = http("POST", f"{ingest}/handshake", handshake_body)
    report.check(
        "a handshake replaying the same nonce is refused",
        replayed.status != 200,
        f"HTTP {replayed.status}. The nonce {nonce} was accepted twice, so a captured "
        "handshake can be replayed inside the window.",
    )

    # --- Authenticated ingestion ---------------------------------------------

    report.section("Ingestion")

    # environment and status are the two the contract requires; the rest is what
    # makes a heartbeat worth reading.
    beat = {
        "environment": environment,
        "status": "healthy",
        "storeVersion": store_version,
        "detail": "Sent by the KNIGHT conformance checker.",
    }

    heartbeat = http("POST", f"{ingest}/heartbeat", beat, authorised)
    report.check(
        "a heartbeat with the store token is accepted",
        heartbeat.status in (200, 202, 204),
        f"HTTP {heartbeat.status}: {heartbeat.text[:300]}",
    )

    # The same body, so a refusal here can only be about the missing token.
    unauthenticated = http("POST", f"{ingest}/heartbeat", beat)
    report.check(
        "a heartbeat without the token is refused",
        unauthenticated.status in (401, 403),
        f"HTTP {unauthenticated.status}",
    )

    # --- Entitlements ---------------------------------------------------------

    report.section("Entitlements")

    features = http("GET", f"{ingest}/features", None, authorised)
    if report.check(
        "the entitlement set can be pulled",
        features.status == 200,
        f"HTTP {features.status}: {features.text[:300]}",
    ):
        payload = features.json()
        report.check(
            "the set is for this store",
            str(payload.get("storeId")) == str(store_id),
            f"expected {store_id}, got {payload.get('storeId')}",
        )
        report.check(
            "the signature verifies with the key from the handshake",
            bool(signing_key)
            and hmac.compare_digest(
                sign(signing_key, entitlement_canonical_form(payload)),
                str(payload.get("signature", "")),
            ),
            "A set whose signature does not verify must be discarded, not enforced. "
            f"canonical form: {entitlement_canonical_form(payload)}",
        )
        report.check(
            "the signature version is one this contract knows",
            str(payload.get("signatureVersion")) == SIGNATURE_VERSION,
        )
        slugs = [feature.get("slug") for feature in payload.get("features", [])]
        report.ok("entitled features", ", ".join(slugs) if slugs else "none")

    return _check_store(report, store_url, store_id, signing_key, environment, session)


def _check_store(
    report: Report,
    store_url: str | None,
    store_id: str,
    signing_key: str,
    environment: str,
    session: dict[str, Any],
) -> int:
    """
    The half a store has to implement itself: the endpoints KNIGHT calls in on.

    Everything above this point works as soon as a store can make an HTTP
    request. Everything below it is code somebody has to write, which is why it
    is where a new integration usually fails.
    """
    if not store_url:
        report.section("The store's own endpoints")
        report.warn(
            "not checked",
            "Pass --store-url to exercise the endpoints KNIGHT calls in on. Until they "
            "work, the store will never leave Pending, whatever else it reports.",
        )
        return report.summary()

    store_url = store_url.rstrip("/")

    # --- Health ---------------------------------------------------------------

    report.section("The store's health endpoint")

    unsigned = http("GET", f"{store_url}{HEALTH_PATH}")
    if unsigned.status == 0:
        report.fail(f"the store answers on {store_url}", unsigned.text)
        print("")
        print("  Nothing about the store's own endpoints can be checked until it answers.")
        return report.summary()

    report.check(
        "an unsigned request is refused",
        unsigned.status in (401, 403),
        f"HTTP {unsigned.status}. This payload lists the store's version, its dependencies "
        "and its installed features, which is exactly the reconnaissance an attacker wants.",
    )

    timestamp = str(int(time.time()))
    nonce = secrets.token_hex(12)
    signed_headers = {
        "X-Knight-Store": str(store_id),
        "X-Knight-Timestamp": timestamp,
        "X-Knight-Nonce": nonce,
        "X-Knight-Signature-Version": SIGNATURE_VERSION,
        "X-Knight-Signature": sign(
            signing_key, request_canonical_form("GET", HEALTH_PATH, timestamp, nonce)
        ),
    }

    signed = http("GET", f"{store_url}{HEALTH_PATH}", None, signed_headers)
    if report.check(
        "a correctly signed request is accepted",
        signed.status == 200,
        f"HTTP {signed.status}: {signed.text[:300]}\n"
        f"       canonical form: {request_canonical_form('GET', HEALTH_PATH, timestamp, nonce)}",
    ):
        try:
            health = signed.json()
        except json.JSONDecodeError:
            report.fail("the health response is JSON", signed.text[:200])
            return report.summary()

        missing = [field for field in REQUIRED_HEALTH_FIELDS if field not in health]
        report.check(
            "it carries every field the contract requires",
            not missing,
            f"missing: {', '.join(missing)}",
        )
        report.check(
            "status is one of healthy, degraded, unhealthy",
            health.get("status") in HEALTH_STATUSES,
            f"got {health.get('status')!r}",
        )
        report.check(
            "it reports the environment it was registered as",
            health.get("environment") == environment,
            f"KNIGHT registered this store as {environment}; the store says "
            f"{health.get('environment')!r}. A store answering for the wrong environment is "
            "the one mistake this contract cannot detect any other way.",
        )
        report.check(
            "dependencies is an object",
            isinstance(health.get("dependencies"), dict),
        )
        if isinstance(health.get("version"), str) and health["version"]:
            report.ok("it reports a store version", health["version"])

    # A signature is only worth something if a wrong one is refused.
    tampered = dict(signed_headers)
    tampered["X-Knight-Signature"] = sign(
        signing_key, request_canonical_form("GET", "/api/knight/something-else", timestamp, nonce)
    )
    report.check(
        "a signature over a different path is refused",
        http("GET", f"{store_url}{HEALTH_PATH}", None, tampered).status in (401, 403),
        "The signature is being accepted without being checked against this request.",
    )

    stale = dict(signed_headers)
    stale_timestamp = str(int(time.time()) - 3600)
    stale["X-Knight-Timestamp"] = stale_timestamp
    stale["X-Knight-Signature"] = sign(
        signing_key, request_canonical_form("GET", HEALTH_PATH, stale_timestamp, nonce)
    )
    report.check(
        "a correctly signed but hour-old request is refused",
        http("GET", f"{store_url}{HEALTH_PATH}", None, stale).status in (401, 403),
        "Without a clock-skew window, a captured request can be replayed indefinitely.",
    )

    # --- Domain verification --------------------------------------------------

    report.section("Domain ownership")

    published = http("GET", f"{store_url}{DOMAIN_VERIFICATION_PATH}")
    if session.get("domainVerificationOutstanding"):
        expected = session.get("domainVerificationToken")
        if published.status != 200:
            report.fail(
                f"{DOMAIN_VERIFICATION_PATH} is served",
                f"HTTP {published.status}. KNIGHT will not move this store to Connected until "
                "it can read the token there.",
            )
        elif expected and published.text != str(expected).strip():
            report.fail(
                "the published token matches the one KNIGHT issued",
                "It is compared exact after trimming: a page that merely contains the token "
                "is not enough.",
            )
        else:
            report.ok("the verification token is published")
    elif published.status == 200:
        report.ok("the verification path is served", "no verification is outstanding")
    else:
        report.ok("no domain verification is outstanding")

    return report.summary()


# --- CLI ----------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Check a store against the KNIGHT store-integration contract.",
        epilog="Run selftest first. It needs nothing running and proves this checker still "
               "agrees with the contract.",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("selftest", help="Reproduce the contract's signed strings. Needs nothing running.")

    check_parser = subparsers.add_parser("check", help="Exercise the contract against a live KNIGHT and store.")
    check_parser.add_argument("--knight-url", required=True, help="e.g. https://knight.example.com")
    check_parser.add_argument("--client-id", required=True, help="From the credential issued in the dashboard.")
    check_parser.add_argument(
        "--client-secret",
        required=True,
        help="Shown exactly once when the credential was issued. If it is lost, rotate it.",
    )
    check_parser.add_argument("--environment", default="Production", choices=["Development", "Staging", "Production"])
    check_parser.add_argument(
        "--store-url",
        help="The store's own base URL. Without it the endpoints KNIGHT calls in on are not checked.",
    )
    check_parser.add_argument("--store-version", default="0.0.0-conformance")
    check_parser.add_argument("--runtime", default="knight-conformance")

    args = parser.parse_args(argv)

    if args.command == "selftest":
        return selftest()

    return check(
        args.knight_url,
        args.client_id,
        args.client_secret,
        args.environment,
        args.store_url,
        args.store_version,
        args.runtime,
    )


if __name__ == "__main__":
    sys.exit(main())
