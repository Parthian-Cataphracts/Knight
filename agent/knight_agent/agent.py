"""
The KNIGHT agent.

A small daemon that runs on a managed server, tells KNIGHT how the machine is,
and carries out the delivery jobs queued for the stores on it.

Three properties define what this is allowed to be (risks.md R22):

- **It executes a closed vocabulary, never a command.** KNIGHT can ask it to
  apply a store's queued installation jobs. There is no endpoint, field or code
  path here that takes a command, a path or a script to run. A compromised control
  plane must not become arbitrary code execution across every managed server at
  once.
- **It reaches out; nothing reaches in.** The agent opens connections to KNIGHT
  and listens on no port, so a managed server needs no inbound rule.
- **Its credential is revocable and machine-bound.** It authenticates with an
  opaque secret compared against a stored hash, so revoking it takes effect on the
  next call rather than when a token happens to expire.

State — the agent id and its credential — lives in one file with restrictive
permissions. The credential is the whole of the agent's authority, so the file is
the thing to protect on the box.
"""

from __future__ import annotations

import json
import logging
import os
import signal
import stat
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

from . import telemetry

logger = logging.getLogger("knight.agent")

DEFAULT_STATE_PATH = Path(os.environ.get("KNIGHT_AGENT_STATE", "/var/lib/knight/agent.json"))


class AgentError(RuntimeError):
    """The agent cannot continue."""


@dataclass
class AgentState:
    """What the agent knows about itself between restarts."""

    agent_id: str
    server_id: str
    credential: str
    heartbeat_seconds: int = 60

    @classmethod
    def load(cls, path: Path) -> "AgentState | None":
        if not path.exists():
            return None

        try:
            raw = json.loads(path.read_text(encoding="utf-8"))
            return cls(
                agent_id=raw["agentId"],
                server_id=raw["serverId"],
                credential=raw["credential"],
                heartbeat_seconds=int(raw.get("heartbeatSeconds", 60)),
            )
        except (json.JSONDecodeError, KeyError, OSError, ValueError) as exc:
            # Refused rather than treated as "not enrolled". Enrolling again on a
            # corrupt file would burn a second provisioning token and leave two
            # agent records for one machine.
            raise AgentError(
                f"The agent state at {path} exists but could not be read: {exc}. "
                "Fix or remove it deliberately rather than letting the agent re-enrol."
            ) from exc

    def save(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)

        document = {
            "agentId": self.agent_id,
            "serverId": self.server_id,
            "credential": self.credential,
            "heartbeatSeconds": self.heartbeat_seconds,
        }

        # Written with restrictive permissions before anything goes in it, so the
        # credential is never briefly world-readable.
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, stat.S_IRUSR | stat.S_IWUSR)
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            json.dump(document, handle, indent=2)


class KnightAgent:
    """Enrols, reports, and applies queued jobs."""

    def __init__(
        self,
        base_url: str,
        state_path: Path = DEFAULT_STATE_PATH,
        store_paths: list[Path] | None = None,
        timeout: int = 15,
    ) -> None:
        self._base_url = base_url.rstrip("/")
        self._state_path = state_path
        self._store_paths = store_paths or []
        self._timeout = timeout
        self._state: AgentState | None = None
        self._running = True

    # --- Enrolment ----------------------------------------------------------

    def enrol(self, provisioning_token: str, version: str) -> AgentState:
        """
        Exchanges a one-time provisioning token for a lasting credential.

        Refuses to run if the agent has already enrolled. Enrolling twice would
        burn a second token and leave KNIGHT with two agent records for one
        machine, only one of which anybody is watching.
        """
        if AgentState.load(self._state_path) is not None:
            raise AgentError(
                f"This machine has already enrolled ({self._state_path}). "
                "Revoke the existing agent in KNIGHT and remove the state file to enrol again."
            )

        payload = {
            "provisioningToken": provisioning_token,
            "version": version,
            "capabilities": json.dumps({"jobs": ["feature-install"], "platform": sys.platform}),
        }

        body = self._post("/api/v1/agent/enrol", payload, authenticated=False)

        state = AgentState(
            agent_id=body["agentId"],
            server_id=body["serverId"],
            credential=body["credential"],
            heartbeat_seconds=int(body.get("heartbeatIntervalSeconds", 60)),
        )

        state.save(self._state_path)
        self._state = state

        logger.info("Enrolled as agent %s on server %s.", state.agent_id, state.server_id)
        return state

    # --- The loop -----------------------------------------------------------

    def run(self, once: bool = False) -> None:
        """Heartbeats on the interval KNIGHT dictates, applying jobs as they appear."""
        self._state = AgentState.load(self._state_path)

        if self._state is None:
            raise AgentError(
                "This agent has not enrolled. Run `knight-agent enrol --token <provisioning token>` first."
            )

        self._install_signal_handlers()

        while self._running:
            try:
                self.heartbeat()
                self.apply_jobs()
            except Exception:  # noqa: BLE001 - a loop that dies on one bad pass is not a monitor
                logger.exception("An agent pass failed; retrying on the next interval.")

            if once:
                return

            # The interval is KNIGHT's decision, taken from its last answer. An
            # agent that chose its own could quietly stop being monitored by
            # choosing a long one.
            self._sleep(self._state.heartbeat_seconds)

    def heartbeat(self) -> dict:
        """Reports that the machine is alive, with a sample of how it is doing."""
        sample = telemetry.collect()

        body = self._post(
            "/api/v1/agent/heartbeat",
            {"version": _version(), "capabilities": None, "metrics": sample.as_payload()},
        )

        interval = int(body.get("heartbeatIntervalSeconds", 0))
        if interval > 0 and self._state is not None and interval != self._state.heartbeat_seconds:
            self._state.heartbeat_seconds = interval
            self._state.save(self._state_path)

        logger.debug("Heartbeat sent; server is %s.", body.get("serverStatus"))
        return body

    def apply_jobs(self) -> int:
        """
        Runs any installation jobs queued for the stores on this machine.

        The whole of what KNIGHT can ask this agent to do. Each store is a
        directory the operator configured; the agent invokes that store's own
        management command with a fixed argument list, never a shell, and never a
        path that came from KNIGHT.
        """
        applied = 0

        for store_path in self._store_paths:
            manage = store_path / "manage.py"

            if not manage.exists():
                logger.warning("Configured store path %s has no manage.py; skipping.", store_path)
                continue

            try:
                completed = subprocess.run(  # noqa: S603 - fixed argv, never shell=True
                    [sys.executable, str(manage), "knight_apply_job"],
                    capture_output=True,
                    text=True,
                    timeout=1800,
                    check=False,
                    cwd=str(store_path),
                )
            except subprocess.TimeoutExpired:
                logger.error("Applying jobs for %s timed out.", store_path)
                continue

            if completed.returncode != 0:
                logger.error("Applying jobs for %s failed: %s", store_path, (completed.stderr or "").strip()[:500])
                continue

            output = (completed.stdout or "").strip()
            if output and "No installation jobs" not in output:
                logger.info("%s: %s", store_path.name, output)
                applied += 1

        return applied

    # --- HTTP ---------------------------------------------------------------

    def _post(self, path: str, payload: dict, authenticated: bool = True) -> dict:
        headers = {"Content-Type": "application/json", "Accept": "application/json"}

        if authenticated:
            if self._state is None:
                raise AgentError("The agent is not enrolled.")

            headers["X-Knight-Agent-Id"] = self._state.agent_id
            headers["X-Knight-Agent-Credential"] = self._state.credential

        request = urllib.request.Request(
            f"{self._base_url}{path}",
            data=json.dumps(payload).encode("utf-8"),
            headers=headers,
            method="POST",
        )

        try:
            with urllib.request.urlopen(request, timeout=self._timeout) as response:
                body = response.read()
                return json.loads(body) if body else {}
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")

            if exc.code == 401:
                # Revocation is meant to take effect immediately, so a refused
                # credential is a deliberate act by an operator, not a transient
                # error to retry around.
                raise AgentError(
                    "KNIGHT refused this agent's credential. It has probably been revoked; "
                    "provision a new agent for this server."
                ) from exc

            raise AgentError(f"KNIGHT refused the request ({exc.code}): {detail[:300]}") from exc
        except urllib.error.URLError as exc:
            raise AgentError(f"KNIGHT could not be reached: {exc.reason}") from exc

    # --- Lifecycle ----------------------------------------------------------

    def _install_signal_handlers(self) -> None:
        def stop(signum, _frame):
            logger.info("Signal %s received; finishing the current pass and stopping.", signum)
            self._running = False

        for received in (signal.SIGINT, signal.SIGTERM):
            try:
                signal.signal(received, stop)
            except (ValueError, OSError, AttributeError):
                # Not every platform or thread can install handlers, and failing
                # to is not a reason to refuse to run.
                logger.debug("Could not install a handler for %s.", received)

    def _sleep(self, seconds: int) -> None:
        """Sleeps in short steps so a stop signal is acted on promptly."""
        deadline = time.monotonic() + seconds

        while self._running and time.monotonic() < deadline:
            time.sleep(min(1.0, deadline - time.monotonic()))


def _version() -> str:
    from . import __version__

    return __version__
