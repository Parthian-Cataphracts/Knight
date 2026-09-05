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

#: The shared secret the store starts with.
#:
#: Fixed for the drill, because something has to be true before KNIGHT issues
#: anything: this is the secret an operator sets while a store is being brought
#: up, and step 14 replaces it with one KNIGHT mints and rotates
#: (docs/adr/0034-a-shared-secret-has-a-lifetime.md).
SERVICE_SECRET = "drill-shared-secret-not-for-a-deployment"

#: What KNIGHT signs the service's control plane with.
#:
#: Not a store's secret, and it cannot be: a store cannot prove it is a store
#: before it has a secret, and issuing that secret is what the control plane is
#: for. The API must be started with the same value in
#: `ServiceControlPlane__Secrets__subscriptions`, which is what the delivery
#: workflow does.
CONTROL_SECRET = os.environ.get(
    "KNIGHT_SERVICE_CONTROL_SECRET", "drill-control-secret-not-for-a-deployment"
)


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


class Service:
    """
    A Feature's own service, running.

    Started rather than mocked, because the whole of phase 23 is the claim that
    an event genuinely reaches one. A drill that stubbed this would be asserting
    that the store can write a row.
    """

    def __init__(self, root: Path, environment: dict[str, str], port: int) -> None:
        self.root = root
        self.environment = environment
        self.port = port
        self.base_url = f"http://127.0.0.1:{port}"
        self._process: subprocess.Popen | None = None

    def manage(self, *arguments: str, allow_failure: bool = False) -> str:
        completed = subprocess.run(  # noqa: S603 - fixed argv, never shell=True
            [sys.executable, "manage.py", *arguments],
            cwd=str(self.root),
            env={**os.environ, **self.environment},
            capture_output=True,
            text=True,
            timeout=300,
            check=False,
        )

        if completed.returncode != 0 and not allow_failure:
            raise DrillFailed(
                f"the service's `manage.py {' '.join(arguments)}` failed:"
                f"\n{(completed.stderr or completed.stdout)[-2000:]}"
            )

        return completed.stdout

    def start(self) -> None:
        self._process = subprocess.Popen(  # noqa: S603
            [sys.executable, "manage.py", "runserver", f"127.0.0.1:{self.port}", "--noreload"],
            cwd=str(self.root),
            env={**os.environ, **self.environment},
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )

        self.wait_until_up()

    def stop(self) -> None:
        if self._process is not None:
            self._process.terminate()

            try:
                self._process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                self._process.kill()

            self._process = None

    def wait_until_up(self, seconds: int = 60) -> None:
        for _ in range(seconds * 2):
            try:
                with urllib.request.urlopen(f"{self.base_url}/healthz", timeout=2) as response:
                    if response.status == 200:
                        return
            except (urllib.error.URLError, OSError):
                pass

            time.sleep(0.5)

        raise DrillFailed(f"The service at {self.base_url} never became healthy.")

    def is_up(self) -> bool:
        try:
            with urllib.request.urlopen(f"{self.base_url}/healthz", timeout=2) as response:
                return response.status == 200
        except (urllib.error.URLError, OSError):
            return False


class StoreServer:
    """
    The reference store, serving HTTP.

    Needed only from phase 23 on: a proxy route cannot be exercised by a
    management command, and "a merchant's request reaches the service through
    the store" is a claim about a request.
    """

    def __init__(self, store: "Store", port: int) -> None:
        self.store = store
        self.port = port
        self.base_url = f"http://127.0.0.1:{port}"
        self._process: subprocess.Popen | None = None

    def start(self) -> None:
        self._process = subprocess.Popen(  # noqa: S603
            [sys.executable, "manage.py", "runserver", f"127.0.0.1:{self.port}", "--noreload"],
            cwd=str(self.store.root),
            env={**os.environ, **self.store.environment},
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
        )

        # Any HTTP answer means the socket is serving, and that is the whole
        # question. `/api/knight/health` is KNIGHT's own endpoint and answers
        # 401 to anything without a signature — reading that as "not started"
        # made the drill wait sixty seconds for a store that had been up for
        # fifty-nine of them.
        for _ in range(120):
            # Before asking the port: a `runserver` that could not bind exits,
            # and the answer on that port would then be somebody else's process.
            if self._process.poll() is not None:
                output = (self._process.communicate()[0] or "")[-1500:]
                raise DrillFailed(f"The store's server exited immediately:\n{output}")

            try:
                with urllib.request.urlopen(f"{self.base_url}/api/knight/health", timeout=2):
                    return
            except urllib.error.HTTPError:
                return
            except (urllib.error.URLError, OSError):
                pass

            time.sleep(0.5)

        # Whatever it said on the way down. A drill that reported only "it never
        # started" would send somebody to read a log that was thrown away.
        output = ""

        if self._process is not None:
            self._process.terminate()

            try:
                output = (self._process.communicate(timeout=10)[0] or "")[-1500:]
            except subprocess.TimeoutExpired:
                self._process.kill()

        raise DrillFailed(f"The store at {self.base_url} never started serving:\n{output}")

    def stop(self) -> None:
        """
        Stops it, and waits until the port is genuinely free.

        Waiting for the process is not enough: the socket outlives it for a
        moment, and a restart that raced it bound nothing while the old process
        kept answering. The drill then tested an old urlconf and reported a 404
        that had nothing to do with the code under test — which cost an hour, so
        the wait is here rather than in a comment.
        """
        if self._process is not None:
            self._process.terminate()

            try:
                self._process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                self._process.kill()
                self._process.wait(timeout=10)

            self._process = None

        for _ in range(40):
            try:
                with urllib.request.urlopen(f"{self.base_url}/api/knight/health", timeout=1):
                    pass
            except urllib.error.HTTPError:
                pass
            except (urllib.error.URLError, OSError):
                return

            time.sleep(0.25)

        raise DrillFailed(f"Something is still listening on {self.base_url}.")

    def get(self, path: str, headers: dict | None = None):
        """One request at the store, as a browser would make it."""
        request = urllib.request.Request(
            f"{self.base_url}/{path.lstrip('/')}",
            headers=headers or {},
            method="GET",
        )

        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return response.status, response.read().decode(errors="replace")
        except urllib.error.HTTPError as exc:
            return exc.code, exc.read().decode(errors="replace")


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

    # Everything the journey starts, so a failure halfway through does not
    # leave a service and a store listening on ports the next run wants.
    stoppable: list = []

    try:
        journey(knight, arguments, work, run, stoppable)
    except DrillFailed as failure:
        print(f"\nDRILL FAILED: {failure}", file=sys.stderr)
        return 1
    finally:
        for process in stoppable:
            try:
                process.stop()
            except Exception:  # noqa: BLE001
                pass

    print("\nThe delivery drill passed: every step of the journey is still true.")
    return 0


def journey(knight: Knight, arguments, work: Path, run: str, stoppable: list) -> None:
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
        for item in knight.call("GET", "/features?pageSize=100")["items"]
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

    # The subscriptions service, and the store serving HTTP. Both are needed
    # from phase 23 on: an event has to reach something, and a proxy route
    # cannot be exercised by a management command.
    service_database = make_database(f"subs_{run}")
    service = Service(
        REPO / "services" / "subscriptions",
        service_environment(work, service_database),
        port=8140,
    )
    service.manage("migrate")
    service.start()
    stoppable.append(service)
    detail(f"the subscriptions service is up at {service.base_url}")

    store_server = StoreServer(reference, port=8141)
    store_server.start()
    stoppable.append(store_server)
    detail(f"the store is serving at {store_server.base_url}")

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

    # --- The other architecture ----------------------------------------------
    #
    # Everything above delivers code into a store. This delivers a
    # configuration: no archive, no package on disk, and nothing whatsoever in
    # the store's database. It is the half of the catalogue the 150-Feature
    # roadmap depends on, and the assertions that matter are the absences
    # (adr/0033).

    step("Delivering a Feature that is a service rather than a package")

    service_feature = ensure_feature(
        knight,
        "subscriptions",
        "Subscriptions and Recurring Orders",
        "Recurring orders, run as a service.",
    )

    tables_before = set(store_tables(reference))

    # Staged, with the service's address rewritten to the one actually running.
    # The base URL is part of the signed document, so this is a genuinely
    # different publish rather than a runtime override — which is right: where a
    # Feature's service lives is a property of the version, and a store must not
    # be able to point a signed configuration somewhere else.
    staged = stage(
        REPO / "features" / "knight-feature-subscriptions-service",
        work / "subscriptions-service",
        "subscriptions",
    )
    _set_base_url(staged, service.base_url)

    # A version of its own each run, for the reason the tamper test needs one:
    # a published version is immutable, so a second run that rewrote the
    # artifact under the same number would leave KNIGHT holding the first run's
    # digest and every install failing verification. The drill has to be able to
    # run twice against one KNIGHT.
    service_version = _next_patch(knight, service_feature["id"], "2.1")
    _set_version(staged, service_version)

    publish(staged, dist, artifacts, arguments.base_url, knight.token)

    artifact = artifacts / f"subscriptions-{service_version}.json"

    expect(
        artifact.exists(),
        "an external Feature is published as a signed configuration document, not an archive",
    )
    expect(
        not (artifacts / f"subscriptions-{service_version}.zip").exists(),
        "and no archive is built for it at all",
    )

    knight.call("POST", f"/customers/{customer['id']}/entitlements", {"featureId": service_feature["id"]})

    external_plan = knight.call("POST", "/installations/plan", {
        "storeId": store["id"],
        "slug": "subscriptions",
        "versionRange": service_version,
    })

    expect(
        external_plan["isSuccessful"],
        "a store can be planned against for a Feature that runs nowhere near it",
    )

    knight.call("POST", "/installations/install", {
        "storeId": store["id"],
        "slug": "subscriptions",
        "versionRange": service_version,
    })
    run_jobs(reference)

    expect(
        installed_versions(knight, store["id"]).get("subscriptions") == service_version,
        "KNIGHT records the configuration version the store registered",
    )

    entry = registry_entry(store_work, "subscriptions")
    contract = entry.get("extra") or {}

    expect(
        contract.get("architecture") == "external_service",
        "the store's own registry records it as a service rather than a package",
    )
    expect(
        len(contract.get("webhooks") or []) == 4,
        "the store registered every event the Feature subscribed to",
    )
    expect(
        len(contract.get("api_proxies") or []) == 3,
        "and every route it asked the store to forward",
    )
    expect(
        len(contract.get("ui_mounts") or []) == 2,
        "and every place it hangs a screen",
    )

    # The three absences that are the whole point of the architecture.
    expect(
        set(store_tables(reference)) == tables_before,
        "installing it created no table in the store's database - a service has no schema here",
    )
    expect(
        not (store_work / "features" / "subscriptions").exists(),
        "and no package directory, because there is no package",
    )
    expect(
        not _job_named(knight, store["id"], "subscriptions", "migrate"),
        "and no migrate step was ever run for it",
    )

    expect(
        entry["enabled"],
        "the Feature is serving: the store will forward its events and proxy its routes",
    )

    # And withdrawing it stops all of that, without touching the service's own
    # data - which the store never had.
    knight.call(
        "POST",
        f"/customers/{customer['id']}/entitlements/{service_feature['id']}/revoke",
        {"reason": f"delivery drill {run}"},
    )
    run_jobs(reference)

    expect(
        not registry_entry(store_work, "subscriptions")["enabled"],
        "withdrawing the entitlement stops the store forwarding anything to it",
    )
    expect(
        set(store_tables(reference)) == tables_before,
        "and still no table was created or dropped in the store's database",
    )

    # --- The service, for real -----------------------------------------------
    #
    # Everything above proved the store *registered* a configuration. This
    # proves the configuration does something: an order placed in the store
    # reaches a running service, and a request to the store comes back from it.
    #
    # This is phase 23's exit criterion, and the reason the drill now starts two
    # more processes (docs/roadmap.md).

    step("Delivering an event to a service that is actually running")

    # Step 11 revoked this to prove that withdrawing an entitlement stops the
    # store forwarding. Granting it back is not tidying up: re-entitlement is
    # its own path — a customer who renews next week — and it must re-enable
    # what is already registered rather than deliver it again.
    knight.call("POST", f"/customers/{customer['id']}/entitlements", {"featureId": service_feature["id"]})
    run_jobs(reference)

    expect(
        registry_entry(store_work, "subscriptions")["enabled"],
        "re-entitling a customer switches the Feature back on without delivering it again",
    )

    # The store's half of the shared secret is configuration; the service's half
    # is a row. An operator sets both, and this is that operator.
    service.manage(
        "knight_store",
        "add",
        "--slug",
        store["slug"],
        "--store-id",
        store["id"],
        "--secret",
        SERVICE_SECRET,
    )

    detail(f"the service will answer store {store['id']}")

    subscription_reference = f"SUB-{run}"
    _make_subscription(service, store["id"], subscription_reference)

    expect(
        _service_subscriptions(service, store["id"]) == 1,
        "the service holds a subscription for this store and nobody else's",
    )

    # An order, placed by the store's own code. Not a call to `publish`: the
    # point is that the store's business logic announces this without knowing
    # anything about subscribers.
    order_number = _place_order(reference, reference=subscription_reference)
    detail(f"placed order {order_number}")

    expect(
        _queued_deliveries(reference) >= 1,
        "placing an order queued a delivery, without the store making a request",
    )

    counts = _run_deliveries(reference)

    expect(
        counts.get("delivered", 0) >= 1,
        f"the worker delivered it to the running service ({counts})",
    )
    expect(
        _service_saw_order(service, store["id"], subscription_reference, order_number),
        "and the service recorded the order against the subscription - "
        "the event genuinely arrived",
    )

    # And back the other way, through the store's own URL space.
    #
    # The store is restarted first, and that is a real property rather than a
    # test artefact: a urlconf is built once at start-up and a proxy route
    # registered afterwards is not in it. The same restart an in-process Feature
    # needs, for the same reason — `install.requiresRestart` says so on one and
    # the store's own `reload` step says so on the other.
    store_server.stop()
    store_server.start()

    status, answered = store_server.get("subscribe/")

    expect(
        status == 200,
        f"a request to the store's proxy prefix is answered ({status}: {answered[:200]})",
    )
    expect(
        '"service": "subscriptions"' in answered.replace(" ", "").replace('"service":"subscriptions"', '"service": "subscriptions"'),
        f"and what comes back is the service's own answer, not the store's ({answered[:200]})",
    )

    # And the store refuses on the service's behalf, before anything is
    # forwarded. Two independent checks of the same rule, and this is the one
    # that does not depend on the service being correct.
    refused, _ = store_server.get("admin/subscriptions/")

    expect(
        refused == 403,
        f"a staff route is refused by the store when nobody is signed in (got {refused})",
    )

    # --- The gate ------------------------------------------------------------
    #
    # An event survives the service being down. This is what separates a working
    # queue from a lucky one, and it is the phase's gate rather than a nice
    # extra.

    step("Losing the service, and losing no event")

    service.stop()

    expect(not service.is_up(), "the service is down")

    second = _place_order(reference, reference=subscription_reference)
    attempted = _run_deliveries(reference)

    expect(
        attempted.get("delivered", 0) == 0,
        "nothing was delivered while the service was down",
    )
    expect(
        _pending_deliveries(reference) >= 1,
        "and the event is queued rather than lost",
    )

    service.start()
    _make_deliveries_due(reference)
    recovered = _run_deliveries(reference)

    expect(
        recovered.get("delivered", 0) >= 1,
        f"when the service came back, the queued event was delivered ({recovered})",
    )
    expect(
        _service_saw_order(service, store["id"], subscription_reference, second),
        "and the service received the order placed while it was down",
    )

    # --- Secrets, rotation and revocation -------------------------------------
    #
    # Phase 24's gate: rotate a live secret with a request in flight and lose
    # nothing; withdraw an entitlement and watch the next call be refused **by
    # the service**, not only by the store
    # (docs/adr/0034-a-shared-secret-has-a-lifetime.md).
    #
    # Everything before this point ran on a secret an operator typed into both
    # ends. This is KNIGHT taking that over.

    step("Rotating a live secret, and revoking one")

    expect(
        _service_answers(service, store["id"], SERVICE_SECRET) == 200,
        "the store's original secret works before anything is rotated",
    )

    issued = knight.call("POST", "/installations/service-secret", {
        "storeId": store["id"],
        "featureId": service_feature["id"],
        "overlapSeconds": 600,
    })

    detail(f"KNIGHT issued {issued['secretName']} (configuration version {issued['configurationVersion']})")

    expect(
        _service_secrets(service, store["id"]) == 2,
        "the service now holds two usable secrets - the old one has not been cut off",
    )
    expect(
        _service_answers(service, store["id"], SERVICE_SECRET) == 200,
        "and a request signed with the old one is still answered, which is what "
        "makes a rotation a deploy rather than an outage",
    )

    # The store takes delivery of the new one the way it takes delivery of any
    # configuration: a queued job its agent runs.
    run_jobs(reference)

    delivered = _delivered_secret(store_work, "subscriptions", issued["secretName"])

    expect(
        bool(delivered) and delivered != SERVICE_SECRET,
        "the store was given a different secret, down the ordinary configuration path",
    )
    expect(
        _service_answers(service, store["id"], delivered) == 200,
        "and the service answers the secret the store now holds",
    )

    # The store is not restarted. The delivered configuration is read per
    # request on purpose: a value cached at start-up would keep a store signing
    # with a secret whose window is closing, which is the one failure this
    # arrangement exists to avoid.
    status, answered = store_server.get("subscribe/")

    expect(
        status == 200,
        f"the store's proxy still answers, without a restart ({status}: {answered[:120]})",
    )

    # And the other half of the gate. Withdrawing the entitlement stops the
    # store forwarding - already proved in step 11 - and now stops the service
    # answering a store that has not noticed.
    knight.call(
        "POST",
        f"/customers/{customer['id']}/entitlements/{service_feature['id']}/revoke",
        {"reason": f"delivery drill {run} - revocation"},
    )

    expect(
        _service_answers(service, store["id"], delivered) == 401,
        "a store whose entitlement was withdrawn is refused by the service itself, "
        "whatever its own registry still says",
    )
    expect(
        _service_secrets(service, store["id"]) == 0,
        "because its secrets were ended rather than left to expire",
    )

    # Re-entitling puts it back, without an operator touching the service.
    knight.call("POST", f"/customers/{customer['id']}/entitlements", {"featureId": service_feature["id"]})
    run_jobs(reference)

    reissued = knight.call("POST", "/installations/service-secret", {
        "storeId": store["id"],
        "featureId": service_feature["id"],
    })
    run_jobs(reference)

    restored = _delivered_secret(store_work, "subscriptions", reissued["secretName"])

    expect(
        _service_answers(service, store["id"], restored) == 200,
        "and a customer who comes back is serving again with a new credential, "
        "issued rather than typed",
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
        for item in knight.call("GET", "/features?pageSize=100")["items"]
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
    """
    Lets the store claim and run everything waiting for it.

    A run that succeeded at nothing says what the store printed. The failure is
    allowed — a job that fails is something several steps here deliberately
    provoke — but a store that ran nothing at all is usually a store that could
    not start, and the install loop's "did not settle" is a description of the
    symptom rather than of the cause.
    """
    output = store.manage("knight_apply_job", "--max-jobs", "30", allow_failure=True)
    succeeded = output.count("Job succeeded")

    if succeeded == 0 and output.strip():
        detail(f"the store ran nothing: {output.strip().splitlines()[-1][:300]}")

    return succeeded


def make_database(name: str) -> str:
    """
    A database of its own, created fresh.

    The same reasoning as `make_store_database`: a reused database is a drill
    telling the truth about its own environment rather than about the product,
    which is the least useful kind of red.
    """
    import psycopg

    settings = _store_database_settings()

    with psycopg.connect(**{**settings, "dbname": "postgres"}, autocommit=True) as connection:
        connection.execute(f'drop database if exists "{name}"')
        connection.execute(f'create database "{name}"')

    return name


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
        # The store's half of the shared secret with the subscriptions service.
        # The service's half is a row it holds; an operator sets both, and in a
        # deployment KNIGHT issues it per store (phase 24).
        "SUBSCRIPTIONS_SERVICE_SECRET": SERVICE_SECRET,
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




def _job_named(knight: Knight, store_id: str, slug: str, step_name: str) -> bool:
    """
    Whether any job for this Feature ever ran a given step.

    Reads the steps KNIGHT recorded rather than the pipeline it would have
    composed, because the question is what the store actually did.
    """
    for job in knight.call("GET", f"/jobs?storeId={store_id}&pageSize=100")["items"]:
        if job.get("featureSlug") != slug:
            continue

        detail = knight.call("GET", f"/jobs/{job['id']}")

        for recorded in detail.get("steps") or []:
            if recorded.get("name") == step_name or recorded.get("step") == step_name:
                return True

    return False




# --- The service, and the traffic between it and the store -------------------


def _set_base_url(source: Path, base_url: str) -> None:
    """
    Rewrites a staged manifest's service address.

    The base URL is inside the signed document, so this makes a genuinely
    different publish rather than a runtime override. That is the right shape:
    where a Feature's service lives is a property of the version, and a store
    must not be able to point a signed configuration somewhere else.
    """
    manifest = source / "knight_manifest.yaml"
    lines = manifest.read_text(encoding="utf-8").splitlines()
    out = []

    for line in lines:
        if line.strip().startswith("base_url:"):
            indent = line[: len(line) - len(line.lstrip())]
            out.append(f"{indent}base_url: {base_url}")
        else:
            out.append(line)

    manifest.write_text("\n".join(out) + "\n", encoding="utf-8")


def service_environment(work: Path, database: str) -> dict[str, str]:
    return {
        "SUBSCRIPTIONS_DEBUG": "true",
        "SUBSCRIPTIONS_DB_NAME": database,
        "SUBSCRIPTIONS_DB_HOST": os.environ.get("STORE_DB_HOST", "127.0.0.1"),
        "SUBSCRIPTIONS_DB_PORT": os.environ.get("STORE_DB_PORT", "5433"),
        "SUBSCRIPTIONS_DB_USER": os.environ.get("STORE_DB_USER", "knight"),
        "SUBSCRIPTIONS_DB_PASSWORD": os.environ.get("STORE_DB_PASSWORD", "knight"),
        "SUBSCRIPTIONS_LOG_LEVEL": "WARNING",
        # What KNIGHT signs the control plane with. Without it the service
        # refuses that whole surface, which is the right default and the wrong
        # thing for a run whose gate is a rotation.
        "SUBSCRIPTIONS_CONTROL_SECRET": CONTROL_SECRET,
    }


def _make_subscription(service: Service, store_id: str, reference: str) -> None:
    """One subscription on the service, so an order has something to be about."""
    service.manage(
        "shell",
        "-c",
        "from knightlink.models import Store;"
        "from subscriptions import services;"
        f"store = Store.objects.get(store_id='{store_id}');"
        f"services.create(store, '{reference}', amount='25.00', shopper_id=1,"
        " lines=[{'sku': 'COFFEE', 'name': 'Coffee', 'quantity': 1, 'unit_price': '25.00'}]);"
        "print('made')",
    )


def _service_answers(service: Service, store_id: str, secret: str) -> int:
    """
    The status a store gets when it signs a request with `secret`.

    Signed here rather than through the store, because what is being asked is
    whether a *particular* secret still verifies — including one the store has
    already replaced. Going through the store would only ever exercise whichever
    secret the store currently holds.
    """
    path = "/hooks/order-placed"
    body = json.dumps({"externalReference": "", "orderNumber": 0}).encode()
    timestamp = str(int(time.time()))
    nonce = uuid.uuid4().hex
    digest = hashlib.sha256(body).hexdigest()
    # The canonical string, built here independently. Importing the store's
    # copy would let one bug agree with itself.
    message = "\n".join(["POST", path, timestamp, nonce, digest])

    request = urllib.request.Request(
        f"{service.base_url}{path}",
        data=body,
        method="POST",
        headers={
            "Content-Type": "application/json",
            "X-Knight-Store": store_id,
            "X-Knight-Identity": "staff",
            "X-Knight-Subject": "system",
            "X-Knight-Timestamp": timestamp,
            "X-Knight-Nonce": nonce,
            "X-Knight-Signature": "sha256="
            + hmac.new(secret.encode(), message.encode(), hashlib.sha256).hexdigest(),
        },
    )

    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status
    except urllib.error.HTTPError as refusal:
        return refusal.code


def _service_secrets(service: Service, store_id: str) -> int:
    """How many secrets that store may currently sign with. Never their values."""
    output = service.manage(
        "shell",
        "-c",
        "from knightlink.models import Store;"
        f"store = Store.objects.filter(store_id='{store_id}').first();"
        "print(len(store.usable_secrets()) if store else 0)",
    )

    return int(output.strip().splitlines()[-1])


def _delivered_secret(store_work: Path, slug: str, name: str) -> str:
    """
    The secret KNIGHT delivered to the store, out of the file the installer wrote.

    Read from disk rather than asked of KNIGHT, because the question is whether
    the store *has* it. KNIGHT knowing what it sent proves nothing about what
    arrived.
    """
    path = store_work / "features" / f"{slug}.config.json"

    if not path.is_file():
        return ""

    document = json.loads(path.read_text(encoding="utf-8"))

    return str((document.get("secrets") or {}).get(name) or "")


def _service_subscriptions(service: Service, store_id: str) -> int:
    output = service.manage(
        "shell",
        "-c",
        "from subscriptions.models import Subscription;"
        f"print(Subscription.objects.filter(store__store_id='{store_id}').count())",
    )

    return int(output.strip().splitlines()[-1])


def _service_saw_order(service: Service, store_id: str, reference: str, order_number: int) -> bool:
    """
    Whether the service recorded that order against that subscription.

    Read out of the service's **own database**, not out of a log line or out of
    a response the store showed us. The claim being tested is that the event
    arrived and was acted on, and only the service's own tables can settle that.
    """
    output = service.manage(
        "shell",
        "-c",
        "from subscriptions.models import SubscriptionEvent;"
        "print(SubscriptionEvent.objects.filter("
        f"subscription__store__store_id='{store_id}',"
        f"subscription__reference='{reference}',"
        f"reason__contains='order {order_number}').exists())",
    )

    return "True" in output


def _place_order(reference_store: Store, *, reference: str) -> int:
    """
    Places a real order through the store's own model, and returns its number.

    `Order.place`, not a hand-written row: the whole point is that the store's
    business code announces this without knowing anything about subscribers.
    """
    output = reference_store.manage(
        "shell",
        "-c",
        "from decimal import Decimal;"
        "from apps.orders.models import Order;"
        "order = Order.place("
        f"external_reference='{reference}', "
        "subtotal=Decimal('25.00'), total=Decimal('25.00'));"
        "print(order.number)",
    )

    return int(output.strip().splitlines()[-1])


def _queued_deliveries(reference_store: Store) -> int:
    output = reference_store.manage(
        "shell",
        "-c",
        "from knight_integration.external.delivery import WebhookDelivery;"
        "print(WebhookDelivery.objects.count())",
    )

    return int(output.strip().splitlines()[-1])


def _pending_deliveries(reference_store: Store) -> int:
    output = reference_store.manage(
        "shell",
        "-c",
        "from knight_integration.external.delivery import WebhookDelivery, DeliveryState;"
        "print(WebhookDelivery.objects.filter(state=DeliveryState.PENDING).count())",
    )

    return int(output.strip().splitlines()[-1])


def _make_deliveries_due(reference_store: Store) -> None:
    """Brings the retry clock forward, rather than waiting thirty seconds for it."""
    reference_store.manage(
        "shell",
        "-c",
        "from django.utils import timezone;"
        "from knight_integration.external.delivery import WebhookDelivery, DeliveryState;"
        "WebhookDelivery.objects.filter(state=DeliveryState.PENDING)"
        ".update(next_attempt_at=timezone.now());"
        "print('due')",
    )


def _run_deliveries(reference_store: Store) -> dict[str, int]:
    """
    One pass of the store's delivery worker, through the real command.

    `manage.py knight_deliver`, because that is what a store runs on a timer and
    a drill that called the function directly would not have exercised it.
    """
    output = reference_store.manage("knight_deliver", allow_failure=True)
    counts = {"delivered": 0, "retrying": 0, "dead": 0}

    for line in output.splitlines():
        for key in counts:
            marker = f" {key}"

            if marker in line:
                for word in line.replace(",", " ").split():
                    if word.isdigit():
                        counts[key] = int(word)
                        break

    # The command prints "N delivered, N retrying, N dead-lettered." Parsed
    # rather than re-derived, so the drill reads what an operator would.
    parts = [part.strip() for part in output.replace(".", "").split(",")]

    for part in parts:
        pieces = part.split()

        if len(pieces) >= 2 and pieces[0].isdigit():
            word = pieces[1].lower()

            if word.startswith("deliver"):
                counts["delivered"] = int(pieces[0])
            elif word.startswith("retry"):
                counts["retrying"] = int(pieces[0])
            elif word.startswith("dead"):
                counts["dead"] = int(pieces[0])

    return counts


if __name__ == "__main__":
    sys.exit(main())
