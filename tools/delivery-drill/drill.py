#!/usr/bin/env python
"""
The delivery drill: the whole journey a customer's Feature takes, run as one
command.

Publish, onboard, connect, install, upgrade, roll back, withdraw — against a real
KNIGHT and a real store, asserting at every step and exiting non-zero on the
first thing that is not true.

**Why this exists.** Phase 18 walked this journey by hand and found eight
defects, six of which made delivery impossible and had been that way for six
phases. Every one of them now has a unit or integration test, and not one of
those tests would find the *next* one: they check the code each defect happened
to be in, and what the eight had in common was the path, not the code. Nothing
else in this repository runs that path — a Feature is authored with
`knight_install_local`, which exists precisely to bypass it.

**What it is not.** Not a unit test and not a substitute for one. It is slow, it
needs a database and two processes, and when it fails it tells you which step
broke rather than which line. That is the right trade for a path whose failures
are otherwise invisible.

Usage:

    python tools/delivery-drill/drill.py --base-url http://localhost:5008 \\
        --admin-email admin@knight.dev --admin-password '…' --totp-secret BASE32

Everything it creates is named with a run id, so two drills against the same
database do not collide and a failed run leaves evidence behind rather than
tidying it away.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import os
import shutil
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DRILL = Path(__file__).resolve().parent

#: Real Features the drill installs alongside its own, so it proves the actual
#: packages still install rather than only that the machinery moves. Three is
#: enough to include a dependency edge: analytics-reports needs analytics-core.
REAL_FEATURES = ["analytics-core", "analytics-reports", "gift-cards"]


class DrillFailed(RuntimeError):
    """A step of the journey was not true. Carries what was expected."""


# --- Reporting ---------------------------------------------------------------

_STEP = 0


def step(message: str) -> None:
    global _STEP
    _STEP += 1
    print(f"\n[{_STEP:02d}] {message}", flush=True)


def detail(message: str) -> None:
    print(f"      {message}", flush=True)


def expect(condition: bool, what: str) -> None:
    """
    Asserts one thing about the journey.

    Every check goes through here so that a failure names what was expected in
    the drill's own words. A drill that failed with an AssertionError and a line
    number would be a drill somebody has to read to use.
    """
    if not condition:
        raise DrillFailed(what)

    detail(f"ok: {what}")


# --- Talking to KNIGHT -------------------------------------------------------


class Knight:
    """The control plane, as an operator's browser would use it."""

    def __init__(self, base_url: str, email: str, password: str, totp_secret: str, secret_file: Path | None = None) -> None:
        self.base_url = base_url.rstrip("/")
        self._email = email
        self._password = password
        self._token = ""
        self._token_taken_at = 0.0

        # A secret the drill enrolled on an earlier run, remembered in the work
        # directory rather than printed. CI creates a fresh administrator every
        # time and never reaches this; a person running the drill twice against
        # the same KNIGHT otherwise has to bootstrap a new account for the second
        # run, because enrolment only works once.
        #
        # A file rather than stdout on purpose: a second factor belongs in
        # neither a terminal scrollback nor a CI log, however throwaway the
        # account is.
        self._secret_file = secret_file
        self._totp_secret = totp_secret or self._remembered()

    def _remembered(self) -> str:
        if self._secret_file and self._secret_file.exists():
            return self._secret_file.read_text(encoding="utf-8").strip()

        return ""

    def _totp(self) -> str:
        """
        A six-digit code from the enrolled secret.

        Implemented rather than imported: the drill has to run in CI with nothing
        installed beyond what the repository already needs, and this is thirty
        lines of the standard.
        """
        key = base64.b32decode(self._totp_secret, casefold=True)
        counter = int(time.time()) // 30
        digest = hmac.new(key, struct.pack(">Q", counter), hashlib.sha1).digest()
        offset = digest[-1] & 0x0F
        code = (struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7FFFFFFF) % 1_000_000

        return f"{code:06d}"

    @property
    def token(self) -> str:
        """
        A fresh access token, taken again when the last one is getting old.

        Tokens are short-lived by design and this drill runs for minutes, so
        holding one for the whole run means a 401 halfway through that looks like
        an authorisation defect and is not.
        """
        if self._token and time.time() - self._token_taken_at < 120:
            return self._token

        if not self._totp_secret:
            self._enrol()

            return self._token

        body = self.call(
            "POST",
            "/auth/login",
            {"email": self._email, "password": self._password, "mfaCode": self._totp()},
            token=None,
        )

        token = body.get("accessToken")

        if not token:
            raise DrillFailed(
                f"KNIGHT would not issue a token: {body.get('status')}. "
                "The account needs a password and an enrolled second factor."
            )

        self._token = token
        self._token_taken_at = time.time()

        return token

    def _enrol(self) -> None:
        """
        Enrol a second factor for an account that has never had one.

        A freshly bootstrapped SuperAdmin holds no permissions until it finishes
        enrolment — the API says so in its own comment on `/auth/mfa/enroll` —
        and CI creates exactly that account on every run. Without this the drill
        would need a secret to be carried in as a repository secret, which means
        a shared credential in a workflow whose whole point is that it starts
        from nothing.

        Login answers a token for an account in this state precisely so it can
        get here, so the sequence is the one a person walks on first sign-in:
        sign in, ask for a secret, prove a code from it.
        """
        first = self.call(
            "POST",
            "/auth/login",
            {"email": self._email, "password": self._password, "mfaCode": None},
            token=None,
        )

        token = first.get("accessToken")

        if not token:
            raise DrillFailed(
                f"KNIGHT would not issue a token to enrol with: {first.get('status')}. "
                "Either the password is wrong, or the account already has a second "
                "factor and its secret has to be given to the drill."
            )

        secret = self.call("POST", "/auth/mfa/enroll", {}, token=token).get("secret")

        if not secret:
            raise DrillFailed("KNIGHT began an enrolment without handing back a secret.")

        self._totp_secret = secret

        if self._secret_file:
            self._secret_file.parent.mkdir(parents=True, exist_ok=True)
            self._secret_file.write_text(secret, encoding="utf-8")

        confirmed = self.call("POST", "/auth/mfa/confirm", {"code": self._totp()}, token=token)

        self._token = confirmed.get("accessToken") or token
        self._token_taken_at = time.time()

    def call(self, method: str, path: str, payload=None, token: str | None = ..., expect_status: int | None = None):
        """
        One request. Raises DrillFailed with the body on anything unexpected.

        `expect_status` inverts that for the refusals: a 404 the drill is asking
        KNIGHT to produce is the assertion, so the status is returned rather than
        raised, and a *success* becomes the failure.
        """
        headers = {"Content-Type": "application/json"}
        authorisation = self.token if token is ... else token

        if authorisation:
            headers["Authorization"] = f"Bearer {authorisation}"

        request = urllib.request.Request(
            f"{self.base_url}/api/v1{path}",
            data=json.dumps(payload).encode() if payload is not None else None,
            headers=headers,
            method=method,
        )

        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                body = response.read().decode()

                if expect_status is not None:
                    raise DrillFailed(
                        f"{method} {path} was expected to answer {expect_status} and answered {response.status}."
                    )

                return json.loads(body) if body else {}
        except urllib.error.HTTPError as exc:
            if expect_status is not None and exc.code == expect_status:
                return {"status": exc.code, "body": exc.read().decode(errors="replace")[:400]}

            raise DrillFailed(f"{method} {path} answered {exc.code}: {exc.read().decode(errors='replace')[:400]}") from exc
        except urllib.error.URLError as exc:
            raise DrillFailed(f"{method} {path} could not be reached: {exc.reason}") from exc


# --- Talking to the store ----------------------------------------------------


class Store:
    """The reference store, driven the way its operator would drive it."""

    def __init__(self, root: Path, environment: dict[str, str]) -> None:
        self.root = root
        self.environment = environment

    def manage(self, *arguments: str, allow_failure: bool = False) -> str:
        completed = subprocess.run(  # noqa: S603 - fixed argv, never shell=True
            [sys.executable, "manage.py", *arguments],
            cwd=str(self.root),
            env={**os.environ, **self.environment},
            capture_output=True,
            text=True,
            timeout=900,
            check=False,
        )

        if completed.returncode != 0 and not allow_failure:
            raise DrillFailed(
                f"`manage.py {' '.join(arguments)}` failed:\n{(completed.stderr or completed.stdout)[-2000:]}"
            )

        return completed.stdout or ""


class NodeStore:
    """
    The node reference store, driven the way its operator would drive it.

    A second class rather than a parameter on the first, because the point is
    that these two stores share **nothing** but the contract: different language,
    different commands, different everything. A shared driver would quietly make
    the drill prove less than it looks like it proves.
    """

    def __init__(self, root: Path, environment: dict[str, str]) -> None:
        self.root = root
        self.environment = environment

    def run(self, script: str, *arguments: str) -> str:
        completed = subprocess.run(  # noqa: S603 - fixed argv, never shell=True
            [_npm(), "run", "--silent", script, "--", *arguments],
            cwd=str(self.root),
            env={**os.environ, **self.environment},
            capture_output=True,
            text=True,
            timeout=600,
            check=False,
        )

        if completed.returncode != 0:
            raise DrillFailed(
                f"`npm run {script}` failed:\n{(completed.stderr or completed.stdout)[-2000:]}"
            )

        return completed.stdout


def _npm() -> str:
    """npm, spelled the way this platform spells it."""
    return "npm.cmd" if os.name == "nt" else "npm"


# --- The journey -------------------------------------------------------------


def main() -> int:
    parser = argparse.ArgumentParser(description="Run KNIGHT's delivery drill.")
    parser.add_argument("--base-url", default=os.environ.get("KNIGHT_BASE_URL", "http://localhost:5008"))
    parser.add_argument("--admin-email", default=os.environ.get("KNIGHT_ADMIN_EMAIL", ""))
    parser.add_argument("--admin-password", default=os.environ.get("KNIGHT_ADMIN_PASSWORD", ""))
    parser.add_argument("--totp-secret", default=os.environ.get("KNIGHT_ADMIN_TOTP", ""))
    parser.add_argument("--artifact-root", default=os.environ.get("KNIGHT_ARTIFACT_ROOT", ""))
    parser.add_argument("--work", default=os.environ.get("KNIGHT_DRILL_WORK", ""))
    parser.add_argument(
        "--store-root",
        default=os.environ.get("KNIGHT_DRILL_STORE", str(REPO / "stores" / "reference-store")),
    )
    arguments = parser.parse_args()

    missing = [
        name
        for name, value in [
            ("--admin-email", arguments.admin_email),
            ("--admin-password", arguments.admin_password),
            # Not --totp-secret. An account that has never enrolled one has no
            # secret to pass, and the drill enrols it rather than refusing to
            # start; give the secret only for an account that already has one.
            ("--artifact-root", arguments.artifact_root),
        ]
        if not value
    ]

    if missing:
        print(f"The drill needs {', '.join(missing)}.", file=sys.stderr)
        return 2

    work = Path(arguments.work) if arguments.work else Path(os.environ.get("TMPDIR", "/tmp")) / "knight-drill"
    work.mkdir(parents=True, exist_ok=True)

    run = uuid.uuid4().hex[:8]
    knight = Knight(
        arguments.base_url,
        arguments.admin_email,
        arguments.admin_password,
        arguments.totp_secret,
        # Keyed by account, so two administrators against one work directory do
        # not hand each other the wrong secret.
        secret_file=work / f"totp-{arguments.admin_email}.secret",
    )

    try:
        journey(knight, arguments, work, run)
    except DrillFailed as failure:
        print(f"\nDRILL FAILED: {failure}", file=sys.stderr)
        return 1

    print("\nThe delivery drill passed: every step of the journey is still true.")
    return 0


def journey(knight: Knight, arguments, work: Path, run: str) -> None:
    signing_key = os.environ.get("KNIGHT_SIGNING_KEY", "")

    if not signing_key:
        raise DrillFailed("KNIGHT_SIGNING_KEY must hold the private half of the key KNIGHT trusts.")

    artifacts = Path(arguments.artifact_root)
    dist = work / "dist"

    # --- Publish -------------------------------------------------------------

    step("Publishing the real Features the drill installs")

    for slug in REAL_FEATURES:
        source = REPO / "features" / f"knight-feature-{_package_of(slug)}"
        published = publish(source, dist, artifacts, arguments.base_url, knight.token)
        detail(f"{slug} {published}")

    step("Creating the drill's own Feature and publishing both of its versions")

    feature = ensure_drill_feature(knight, run)
    detail(f"identity {feature['id']}")

    for version in ("1.0.0", "1.1.0"):
        source = DRILL / "versions" / version
        staged = stage(source, work / f"drill-{version}", feature["slug"])
        publish(staged, dist, artifacts, arguments.base_url, knight.token)
        detail(f"{feature['slug']} {version}")

    # A run that died between corrupting an artifact and yanking it leaves a
    # version behind that no store can install and every upgrade resolves to.
    # The drill has to be able to run twice against one KNIGHT, so it clears its
    # own wreckage rather than requiring a clean database to be correct.
    for stale in _published_above(knight, feature["id"], keep=("1.0.0", "1.1.0")):
        knight.call("POST", f"/features/versions/{stale['id']}/yank", {
            "reason": "delivery drill: left behind by a run that did not finish",
        })
        detail(f"yanked {stale['version']}, left over from an earlier run")

    # --- Onboard -------------------------------------------------------------

    step("Onboarding a customer the way an operator would")

    customer = knight.call("POST", "/customers", {
        "name": f"Drill {run}",
        "contactEmail": f"drill-{run}@knight.test",
    })
    activate(knight, f"/customers/{customer['id']}/activate", customer)

    store = knight.call("POST", "/stores", {
        "customerId": customer["id"],
        "name": f"Drill store {run}",
        "slug": f"drill-{run}",
        "primaryDomain": f"drill-{run}.knight.test",
        "environment": "Development",
        "hostingModel": "SharedManaged",
    })
    activate(knight, f"/stores/{store['id']}/activate", store)
    credential = knight.call("POST", f"/stores/{store['id']}/credentials")

    expect(bool(credential.get("clientSecret")), "the credential is returned exactly once, at issue")

    plan = _plan_named(knight, "Professional")
    dedicated = {
        item["slug"]
        for item in knight.call("GET", "/features?pageSize=200")["items"]
        if item.get("requiresDedicatedInfrastructure")
    }
    toggleable = [
        entry["featureId"]
        for entry in plan.get("features", [])
        if entry.get("isCustomerToggleable") and entry.get("featureSlug") not in dedicated
    ]

    subscription = knight.call("POST", "/subscriptions", {
        "customerId": customer["id"],
        "planId": plan["id"],
        "featureIds": toggleable,
    })
    activate(knight, f"/subscriptions/{subscription['id']}/activate", subscription)

    # The drill's own Feature is not in any plan, so it is granted directly —
    # which is the platform's own lever and a path worth exercising too.
    knight.call("POST", f"/customers/{customer['id']}/entitlements", {"featureId": feature["id"]})

    detail(f"customer {customer['id']} store {store['id']}")

    # --- Connect -------------------------------------------------------------

    step("Connecting the store, and checking KNIGHT learned what it runs")

    store_work = work / f"store-{run}"
    shutil.rmtree(store_work, ignore_errors=True)
    (store_work / "features").mkdir(parents=True, exist_ok=True)

    database = make_store_database(run)
    detail(f"store database {database}")

    reference = Store(Path(arguments.store_root), store_environment(credential, store, store_work, database))

    reference.manage("migrate")
    reference.manage("knight_register")
    reference.manage("knight_heartbeat")

    context = knight.call("POST", "/installations/plan", {
        "storeId": store["id"],
        "slug": "analytics-core",
        "versionRange": None,
    })

    # The single most valuable assertion in the drill. A store that has not
    # reported its runtime cannot be certified for anything, and phase 18 found
    # that no store ever could: the heartbeat had nowhere to say.
    expect(
        context["isSuccessful"],
        "a connected store can be planned against — it reported its runtime and its database",
    )

    # --- Install -------------------------------------------------------------

    step("Installing everything the store is entitled to")

    # The drill's own Feature is pinned to 1.0.0 so the journey has somewhere to
    # upgrade *from*. Everything else takes whatever is newest, which is what an
    # operator installing from the catalogue actually does.
    wanted = {slug: None for slug in REAL_FEATURES}
    wanted[feature["slug"]] = "1.0.0"

    install_all(knight, reference, store["id"], wanted)

    installed = installed_versions(knight, store["id"])

    for slug in wanted:
        expect(slug in installed, f"{slug} is installed on the store")

    expect(
        installed["analytics-core"] == "1.1.0",
        "an install with no version named takes the newest published one",
    )
    expect(
        installed[feature["slug"]] == "1.0.0",
        "an install pinned to a version gets that version and not the newest",
    )

    reference.manage("migrate", allow_failure=True)
    expect(
        "knight_drill_record" in store_tables(reference),
        "the delivered Feature's migrations ran against the store's database",
    )

    # --- The row that has to survive -----------------------------------------

    step("Writing a row that has to survive both directions")

    reference.manage(
        "shell",
        "-c",
        "from knight_feature_drill.models import DrillRecord;"
        f"DrillRecord.objects.get_or_create(reference='{run}');"
        "print(DrillRecord.objects.count())",
    )

    expect(drill_rows(reference) == 1, "the row is there to begin with")
    expect(
        "note" not in drill_columns(reference),
        "1.0.0 does not have 1.1.0's column, so the upgrade has something to apply",
    )

    # --- Upgrade -------------------------------------------------------------

    step("Upgrading to 1.1.0 with no version named")

    result = knight.call("POST", "/installations/upgrade", {
        "storeId": store["id"],
        "slug": feature["slug"],
        "versionRange": None,
    })

    expect(
        bool(result.get("jobs")),
        "an upgrade with no version named queues a job — it must not resolve to what is already installed",
    )

    run_jobs(reference)

    expect(
        installed_versions(knight, store["id"])[feature["slug"]] == "1.1.0",
        "the store is on 1.1.0",
    )
    expect("note" in drill_columns(reference), "the upgrade applied 1.1.0's migration")
    expect(drill_rows(reference) == 1, "the row survived the upgrade")

    # --- Roll back -----------------------------------------------------------

    step("Rolling back to 1.0.0")

    knight.call("POST", "/installations/rollback", {
        "storeId": store["id"],
        "featureId": feature["id"],
        "reason": f"delivery drill {run}",
    })
    run_jobs(reference)

    expect(
        installed_versions(knight, store["id"])[feature["slug"]] == "1.0.0",
        "KNIGHT records the version the store rolled back to, not the one it left",
    )
    expect(
        drill_rows(reference) == 1,
        "the row survived the rollback — a rollback that loses data has failed however cleanly it reported",
    )
    expect(
        "note" not in drill_columns(reference),
        "the rollback reversed 1.1.0's migration rather than leaving the schema where it was",
    )

    # --- Refusals ------------------------------------------------------------
    #
    # Everything above asserts that something worked. A delivery engine is
    # judged at least as much on what it refuses, and until phase 20 those paths
    # rested on unit tests alone - which is exactly the position the whole
    # delivery path was in before phase 18.

    step("Refusing what must be refused")

    conformance = ensure_feature(
        knight,
        "node-conformance",
        "Node Conformance",
        "The Feature a node store takes delivery of. Not for sale.",
    )
    publish(REPO / "features" / "knight-feature-node-conformance", dist, artifacts, arguments.base_url, knight.token)
    knight.call("POST", f"/customers/{customer['id']}/entitlements", {"featureId": conformance["id"]})

    mismatch = knight.call("POST", "/installations/plan", {
        "storeId": store["id"],
        "slug": "node-conformance",
        "versionRange": None,
    })

    expect(
        not mismatch["isSuccessful"],
        "a Feature built for another runtime is refused for this store",
    )
    expect(
        any(failure["code"] == "RuntimeMismatch" for failure in mismatch["failures"]),
        "and refused as a runtime mismatch, not as a version nobody can bump",
    )

    refused = knight.call("POST", "/installations/install", {
        "storeId": store["id"],
        "slug": "node-conformance",
        "versionRange": None,
    })

    expect(
        not refused["plan"]["isSuccessful"] and not refused["jobs"],
        "a refused plan queues no job — the refusal is not advisory",
    )

    # A tampered artifact. Published properly, then the bytes on the artifact
    # store are changed underneath it, which is the shape of the attack the
    # digest and the signature exist for: everything KNIGHT said is true and the
    # thing that arrives is not what it said.
    # A version of its own, one higher every run. Reusing a fixed number looked
    # tidier and was wrong: the cleanup above yanks the corrupted version a
    # previous run published, the second `publish` of the same number is refused
    # as "already exists", and the upgrade then resolves to nothing at all — so
    # the two assertions below passed while nothing whatsoever had been tested.
    # A vacuous green is worse than a red.
    tamper_version = _next_patch(knight, feature["id"], "1.2")
    tampered = stage(DRILL / "versions" / "1.1.0", work / f"drill-{tamper_version}", feature["slug"])
    _set_version(tampered, tamper_version)
    publish(tampered, dist, artifacts, arguments.base_url, knight.token)

    artifact = artifacts / f"{feature['slug']}-{tamper_version}.zip"
    expect(artifact.exists(), "the artifact the drill is about to corrupt is where KNIGHT put it")

    # One byte, in place. Appending would change the length and the store would
    # refuse it at `fetch` for being the wrong size — a real refusal, and the
    # wrong one to be testing here: the digest is what stands between a store and
    # an artifact somebody swapped for one of the same size.
    bytes_ = bytearray(artifact.read_bytes())
    bytes_[len(bytes_) // 2] ^= 0xFF
    artifact.write_bytes(bytes(bytes_))

    knight.call("POST", "/installations/upgrade", {
        "storeId": store["id"],
        "slug": feature["slug"],
        "versionRange": tamper_version,
    })
    run_jobs(reference)

    expect(
        installed_versions(knight, store["id"])[feature["slug"]] == "1.0.0",
        "a corrupted artifact does not become an installed version",
    )
    expect(
        drill_rows(reference) == 1,
        "and the store the corrupted artifact was aimed at is untouched",
    )
    tampered_states = _job_states(knight, store["id"], feature["slug"], tamper_version)

    expect(
        tampered_states == {"Failed"},
        "the store reported the failure rather than reporting nothing — a job stuck Running is a Feature "
        f"nobody knows is missing (job states: {sorted(tampered_states) or 'no job at all'})",
    )

    # And then withdraw it, which is what an operator does about a version that
    # cannot be delivered. Not tidying up after the drill: a corrupted version
    # left published is one the *next* upgrade with no version named resolves
    # to, on every store, for ever — which is precisely what happened the first
    # time this ran twice against one KNIGHT, and is the reason `yank` exists.
    knight.call("POST", f"/features/versions/{_version_id(knight, feature['id'], tamper_version)}/yank", {
        "reason": f"delivery drill {run}: artifact corrupted on the store deliberately",
    })

    after = knight.call("POST", "/installations/plan", {
        "storeId": store["id"],
        "slug": feature["slug"],
        "versionRange": None,
    })

    expect(
        all(step["version"] != tamper_version for step in after["steps"]),
        "a yanked version stops being what an upgrade with no version named resolves to",
    )

    # A slug that is genuinely not in the catalogue, which the two endpoints
    # answer differently and deliberately. `plan` is a question — "what would
    # happen" — and "there is no such Feature" is a legitimate answer to it.
    # `install` is an instruction, and there is nothing to instruct.
    #
    # Worth asserting because phase 18 found `install` answering that 404 about
    # fifteen Features that were sitting published in the catalogue: a failed
    # plan carries no steps, and a missing root step was read as a missing
    # Feature. The 404 has to keep working for the case it is actually about.
    unknown = knight.call("POST", "/installations/plan", {
        "storeId": store["id"],
        "slug": f"no-such-feature-{run}",
        "versionRange": None,
    })

    expect(
        any(failure["code"] == "UnknownFeature" for failure in unknown["failures"]),
        "a slug that is not in the catalogue is refused by name rather than by compatibility",
    )

    knight.call(
        "POST",
        "/installations/install",
        {"storeId": store["id"], "slug": f"no-such-feature-{run}", "versionRange": None},
        expect_status=404,
    )

    detail("ok: and installing one is a 404 — the case the false 404 of phase 18 was borrowed from")

    # --- Withdraw ------------------------------------------------------------

    step("Withdrawing the entitlement")

    knight.call(
        "POST",
        f"/customers/{customer['id']}/entitlements/{feature['id']}/revoke",
        {"reason": f"delivery drill {run}"},
    )
    run_jobs(reference)

    expect(
        state_of(knight, store["id"], feature["slug"]) == "Disabled",
        "withdrawing an entitlement disables the Feature without anybody queueing a job",
    )
    expect(
        drill_rows(reference) == 1,
        "a disabled Feature keeps its data — disable is not uninstall",
    )
    expect(
        not registry_entry(store_work, feature["slug"])["enabled"],
        "the store's own registry agrees the Feature is no longer serving",
    )

    # --- The other runtime ---------------------------------------------------

    step("Delivering to a store that is not Django")

    node_store = knight.call("POST", "/stores", {
        "customerId": customer["id"],
        "name": f"Node drill store {run}",
        "slug": f"node-drill-{run}",
        "primaryDomain": f"node-drill-{run}.knight.test",
        "environment": "Development",
        "hostingModel": "SharedManaged",
    })
    activate(knight, f"/stores/{node_store['id']}/activate", node_store)
    node_credential = knight.call("POST", f"/stores/{node_store['id']}/credentials")

    node_work = work / f"node-{run}"
    shutil.rmtree(node_work, ignore_errors=True)
    (node_work / "features").mkdir(parents=True, exist_ok=True)

    node = NodeStore(REPO / "stores" / "node-reference-store", node_environment(node_credential, node_work))
    node.run("connect")

    node_plan = knight.call("POST", "/installations/plan", {
        "storeId": node_store["id"],
        "slug": "node-conformance",
        "versionRange": None,
    })

    # The assertion this whole half exists for. Until phase 20 a node store could
    # not be planned against at all: compatibility was decided on Python and
    # Django versions it has no way to report, so every Feature was refused —
    # the same defect phase 18 found for Django stores, still live for the other
    # runtime three phases later, because nothing had ever asked.
    expect(
        node_plan["isSuccessful"],
        "a node store that has said what it runs can be planned against",
    )

    django_on_node = knight.call("POST", "/installations/plan", {
        "storeId": node_store["id"],
        "slug": "analytics-core",
        "versionRange": None,
    })

    expect(
        any(failure["code"] == "RuntimeMismatch" for failure in django_on_node["failures"]),
        "and a Django Feature is refused for it, by runtime rather than by version",
    )

    knight.call("POST", "/installations/install", {
        "storeId": node_store["id"],
        "slug": "node-conformance",
        "versionRange": None,
    })

    node.run("work")

    expect(
        installed_versions(knight, node_store["id"]).get("node-conformance") == "1.0.0",
        "the node store claimed the job, ran it, and KNIGHT records the version it installed",
    )
    expect(
        node_registry(node_work, "node-conformance")["enabled"],
        "and the node store's own registry agrees it is installed and serving",
    )


# --- The pieces --------------------------------------------------------------


def activate(knight: Knight, path: str, record: dict) -> None:
    """
    Activates something that is not already active.

    KNIGHT creates some records active and some pending, and refuses to activate
    what already is - correctly. The drill is asserting the journey, not
    rediscovering which is which, so it asks only when there is something to ask
    for.
    """
    if record.get("status") == "Active":
        return

    knight.call("POST", path)


def publish(source: Path, dist: Path, artifacts: Path, base_url: str, token: str) -> str:
    """Builds, signs, uploads and publishes one version, through the real tool."""
    completed = subprocess.run(  # noqa: S603
        [
            sys.executable,
            str(REPO / "features" / "tools" / "knight_package.py"),
            "publish",
            str(source),
            "--dist", str(dist),
            "--artifact-root", str(artifacts),
            "--base-url", base_url,
            "--token", token,
        ],
        capture_output=True,
        text=True,
        timeout=300,
        check=False,
    )

    if completed.returncode != 0:
        output = completed.stderr or completed.stdout

        # A published version is immutable, and KNIGHT is right to refuse a
        # second one. The drill wants the version *available*, not to be the
        # one that published it - so on a second run against the same
        # database this is the desired end state rather than a failure.
        if "already exists" in output:
            return "already published"

        raise DrillFailed(f"Publishing {source.name} failed:\n{output[-1500:]}")

    return completed.stdout.strip().splitlines()[-1]


def ensure_drill_feature(knight: Knight, run: str) -> dict:
    """The drill's own Django Feature, the one it moves up and down."""
    return ensure_feature(knight, "delivery-drill", "Delivery Drill", "KNIGHT's own delivery drill. Not for sale.")


def ensure_feature(knight: Knight, slug: str, name: str, description: str) -> dict:
    """
    A catalogue identity, created through the API.

    Created rather than seeded, so the sellable catalogue never carries a test
    fixture. It is published immediately because an unpublished Feature cannot be
    entitled, which is the rule rather than an inconvenience.
    """
    existing = [
        item
        for item in knight.call("GET", "/features?pageSize=200")["items"]
        if item["slug"] == slug
    ]

    if existing:
        feature = existing[0]
    else:
        feature = knight.call("POST", "/features", {
            "slug": slug,
            "name": name,
            "description": description,
            "category": "Operations",
            "isOptional": True,
            "requiresDedicatedInfrastructure": False,
        })

    if feature.get("status") != "Published":
        feature = knight.call("POST", f"/features/{feature['id']}/publish")

    return feature


def stage(source: Path, target: Path, slug: str) -> Path:
    """A copy of one drill version to publish from, so the source tree is untouched."""
    shutil.rmtree(target, ignore_errors=True)
    shutil.copytree(source, target, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))

    return target


def install_all(knight: Knight, store: Store, store_id: str, wanted: dict[str, str | None]) -> None:
    """
    Asks for each Feature and runs whatever that queued, until nothing moves.

    Repeated because a dependency has to be installed before the thing that needs
    it can be planned, and because the engine deliberately runs one job at a time
    per store. "Already has work in flight" is the engine being right.
    """
    for attempt in range(1, 8):
        queued = 0

        for slug, version_range in wanted.items():
            try:
                result = knight.call("POST", "/installations/install", {
                    "storeId": store_id,
                    "slug": slug,
                    "versionRange": version_range,
                })
            except DrillFailed:
                continue

            queued += len(result.get("jobs") or [])

        ran = run_jobs(store)
        detail(f"pass {attempt}: queued {queued}, ran {ran}")

        if queued == 0 and ran == 0:
            return

    raise DrillFailed("The install loop did not settle; something is queueing work that never completes.")


def run_jobs(store: Store) -> int:
    """Lets the store claim and run everything waiting for it."""
    output = store.manage("knight_apply_job", "--max-jobs", "30", allow_failure=True)

    return output.count("Job succeeded")


def make_store_database(run: str) -> str:
    """
    A database of this run's own, created before the store is pointed at it.

    Hermetic on purpose. The first version of this drill reused one store
    database and the second run failed on a `NOT NULL` violation: the table still
    had 1.1.0's column from the run before, while the package on disk was 1.0.0's
    and did not know to fill it. That is a drill telling the truth about its own
    environment rather than about the product, which is the least useful kind of
    red.
    """
    import psycopg

    name = f"knight_drill_{run}"
    settings = _store_database_settings()

    with psycopg.connect(**{**settings, "dbname": "postgres"}, autocommit=True) as connection:
        connection.execute(f'drop database if exists "{name}"')
        connection.execute(f'create database "{name}"')

    return name


def _store_database_settings() -> dict[str, str]:
    return {
        "host": os.environ.get("STORE_DB_HOST", "127.0.0.1"),
        "port": os.environ.get("STORE_DB_PORT", "5433"),
        "user": os.environ.get("STORE_DB_USER", "knight"),
        "password": os.environ.get("STORE_DB_PASSWORD", "knight"),
    }


def store_environment(credential: dict, store: dict, work: Path, database: str) -> dict[str, str]:
    public_key = os.environ.get("KNIGHT_PUBLIC_KEY", "")

    if not public_key:
        raise DrillFailed("KNIGHT_PUBLIC_KEY must hold the public half, so the store can verify what it downloads.")

    return {
        "KNIGHT_BASE_URL": os.environ.get("KNIGHT_BASE_URL", "http://localhost:5008"),
        "KNIGHT_CLIENT_ID": credential["clientId"],
        "KNIGHT_CLIENT_SECRET": credential["clientSecret"],
        "KNIGHT_ENVIRONMENT": "Development",
        "KNIGHT_STORE_ID": store["id"],
        "STORE_VERSION": "1.0.0",
        "KNIGHT_FEATURE_ROOT": str(work / "features"),
        # Keyed by the id the packaging tool signed under, not by a literal:
        # a store that trusts the right key under the wrong name rejects every
        # artifact, and the message it gives is about the signature rather than
        # about the name, which is a bad half-hour.
        "KNIGHT_SIGNING_KEYS": json.dumps({os.environ.get("KNIGHT_SIGNING_KEY_ID", "dev"): public_key}),
        "STORE_DB_NAME": database,
        **{f"STORE_DB_{key.upper()}": str(value) for key, value in _store_database_settings().items()},
        "DJANGO_SECRET_KEY": "delivery-drill-only",
        "DJANGO_DEBUG": "false",
        "DJANGO_ALLOWED_HOSTS": "*",
    }


def installed_versions(knight: Knight, store_id: str) -> dict[str, str]:
    return {
        item["featureSlug"]: item.get("installedVersion")
        for item in knight.call("GET", f"/installations?storeId={store_id}&pageSize=100")["items"]
    }


def state_of(knight: Knight, store_id: str, slug: str) -> str:
    for item in knight.call("GET", f"/installations?storeId={store_id}&pageSize=100")["items"]:
        if item["featureSlug"] == slug:
            return item["state"]

    raise DrillFailed(f"{slug} has no installation row on this store.")


def registry_entry(work: Path, slug: str) -> dict:
    path = work / "features" / "installed.json"

    if not path.exists():
        raise DrillFailed("The store wrote no feature registry at all.")

    return json.loads(path.read_text(encoding="utf-8"))["features"][slug]


def store_tables(store: Store) -> list[str]:
    output = store.manage(
        "shell",
        "-c",
        "from django.db import connection;print(connection.introspection.table_names())",
    )

    return json.loads(output.strip().splitlines()[-1].replace("'", '"'))


def drill_columns(store: Store) -> list[str]:
    output = store.manage(
        "shell",
        "-c",
        "from django.db import connection\n"
        "with connection.cursor() as cursor:\n"
        "    print([c.name for c in connection.introspection.get_table_description(cursor, 'knight_drill_record')])",
    )

    return json.loads(output.strip().splitlines()[-1].replace("'", '"'))


def drill_rows(store: Store) -> int:
    output = store.manage(
        "shell",
        "-c",
        "from django.db import connection\n"
        "with connection.cursor() as cursor:\n"
        "    cursor.execute('select count(*) from knight_drill_record')\n"
        "    print(cursor.fetchone()[0])",
    )

    return int(output.strip().splitlines()[-1])


def _plan_named(knight: Knight, name: str) -> dict:
    for plan in knight.call("GET", "/plans?pageSize=100")["items"]:
        if plan["name"] == name:
            return plan

    raise DrillFailed(f"No '{name}' plan is seeded.")


def _package_of(slug: str) -> str:
    """The package directory for a catalogue slug, which is not always the slug."""
    return {"advanced-promotions": "promotions"}.get(slug, slug)




def _set_version(source: Path, version: str) -> None:
    """
    Rewrites a staged manifest's version.

    So the tampered artifact is a version of its own rather than a second
    publish of one that already exists — KNIGHT refuses that, and rightly.
    """
    manifest = source / "knight_manifest.yaml"
    lines = manifest.read_text(encoding="utf-8").splitlines()

    manifest.write_text(
        "\n".join(f"version: {version}" if line.startswith("version:") else line for line in lines) + "\n",
        encoding="utf-8",
    )


def _job_states(knight: Knight, store_id: str, slug: str, version: str) -> set[str]:
    """
    The states of the jobs for this exact version.

    By version rather than "the most recent job", because the listing is not
    ordered newest-first and the drill leaves several jobs for one Feature
    behind. Asking for the one it means is both correct and cheaper to read.

    Deliberately not "is not succeeded": a job still sitting in `Running`
    because nobody ever reported it is the failure mode phase 18 found, and it
    would pass a weaker check while being exactly the thing worth catching.
    """
    return {
        str(job.get("state"))
        for job in knight.call("GET", f"/jobs?storeId={store_id}&pageSize=100")["items"]
        if job.get("featureSlug") == slug and job.get("targetVersion") == version
    }


def node_environment(credential: dict, work: Path) -> dict[str, str]:
    """
    What the node store needs. Deliberately not `store_environment`: it shares
    the credential and the trusted key and nothing else, because the two stores
    share the contract and nothing else.
    """
    public_key = os.environ.get("KNIGHT_PUBLIC_KEY", "")

    if not public_key:
        raise DrillFailed("KNIGHT_PUBLIC_KEY must hold the public half, so the store can verify what it downloads.")

    return {
        "KNIGHT_BASE_URL": os.environ.get("KNIGHT_BASE_URL", "http://localhost:5008"),
        "KNIGHT_CLIENT_ID": credential["clientId"],
        "KNIGHT_CLIENT_SECRET": credential["clientSecret"],
        "KNIGHT_ENVIRONMENT": "Development",
        "KNIGHT_STORE_VERSION": "1.0.0",
        "KNIGHT_FEATURE_ROOT": str(work / "features"),
        "KNIGHT_WORKSPACE": str(work / "workspace"),
        "KNIGHT_TRUSTED_KEYS": json.dumps({os.environ.get("KNIGHT_SIGNING_KEY_ID", "dev"): public_key}),
    }


def node_registry(work: Path, slug: str) -> dict:
    """One entry out of the node store's own registry, as it wrote it."""
    path = work / "features" / "installed.json"

    if not path.exists():
        raise DrillFailed(f"The node store wrote no registry at {path}.")

    features = json.loads(path.read_text(encoding="utf-8")).get("features", {})

    if slug not in features:
        raise DrillFailed(f"The node store's registry has no entry for '{slug}': {sorted(features)}")

    return features[slug]




def _version_id(knight: Knight, feature_id: str, version: str) -> str:
    for item in knight.call("GET", f"/features/{feature_id}/versions?pageSize=100")["items"]:
        if item["version"] == version:
            return item["id"]

    raise DrillFailed(f"KNIGHT has no version {version} of {feature_id} to act on.")




def _published_above(knight: Knight, feature_id: str, keep: tuple[str, ...]) -> list[dict]:
    """Published versions of the drill's Feature that are not the two it owns."""
    return [
        item
        for item in knight.call("GET", f"/features/{feature_id}/versions?pageSize=100")["items"]
        if item["version"] not in keep and item.get("status") == "Published"
    ]




def _next_patch(knight: Knight, feature_id: str, series: str) -> str:
    """The next unused patch in a series, so every run corrupts a version of its own."""
    used = {
        item["version"]
        for item in knight.call("GET", f"/features/{feature_id}/versions?pageSize=100")["items"]
    }

    for patch in range(1000):
        candidate = f"{series}.{patch}"

        if candidate not in used:
            return candidate

    raise DrillFailed(f"A thousand versions of {series} already exist, which cannot be right.")


if __name__ == "__main__":
    sys.exit(main())
