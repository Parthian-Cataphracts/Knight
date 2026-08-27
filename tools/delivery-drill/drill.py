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

    def __init__(self, base_url: str, email: str, password: str, totp_secret: str) -> None:
        self.base_url = base_url.rstrip("/")
        self._email = email
        self._password = password
        self._totp_secret = totp_secret
        self._token = ""
        self._token_taken_at = 0.0

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

        confirmed = self.call("POST", "/auth/mfa/confirm", {"code": self._totp()}, token=token)

        self._token = confirmed.get("accessToken") or token
        self._token_taken_at = time.time()

    def call(self, method: str, path: str, payload=None, token: str | None = ...):
        """One request. Raises DrillFailed with the body on anything unexpected."""
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
                return json.loads(body) if body else {}
        except urllib.error.HTTPError as exc:
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
    knight = Knight(arguments.base_url, arguments.admin_email, arguments.admin_password, arguments.totp_secret)

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
    """
    The drill's own catalogue identity, created through the API.

    Created rather than seeded, so the sellable catalogue never carries a test
    fixture. It is published immediately because an unpublished Feature cannot be
    entitled, which is the rule rather than an inconvenience.
    """
    slug = "delivery-drill"
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
            "name": "Delivery Drill",
            "description": "KNIGHT's own delivery drill. Not for sale.",
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
        "KNIGHT_SIGNING_KEYS": json.dumps({"dev": public_key}),
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


if __name__ == "__main__":
    sys.exit(main())
