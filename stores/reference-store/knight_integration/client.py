"""
The HTTP client to KNIGHT.

One class, one place where a URL to the control plane is constructed, and one
retry policy. Two properties matter more than anything else here:

- **A slow or absent KNIGHT never becomes the shopper's problem.** Every call
  has a timeout, and every caller in this package is either a background thread
  or a management command. Nothing on a request path waits for the control
  plane.
- **A 401 is recoverable exactly once.** Tokens expire; when one does, the
  client handshakes again and retries the call. If the retry is also refused,
  the credential is wrong and retrying harder will not fix it.
"""

from __future__ import annotations

import logging
import random
import time
import uuid
from typing import Any

import requests

from .conf import KnightSettings, get_settings

logger = logging.getLogger(__name__)


class KnightUnavailable(RuntimeError):
    """KNIGHT could not be reached, or answered with something unusable."""


class KnightRejected(RuntimeError):
    """KNIGHT answered, and refused. Retrying the same call will not help."""

    def __init__(self, status_code: int, detail: str, code: str = "") -> None:
        super().__init__(f"KNIGHT refused the request ({status_code}): {detail}")
        self.status_code = status_code
        self.detail = detail
        self.code = code


class KnightClient:
    """
    Calls KNIGHT's ingestion API.

    Constructed per use rather than kept as a singleton: it holds no state worth
    sharing, and the token it uses lives in the cache, where every worker in
    every process can see it.
    """

    RETRYABLE_STATUS = frozenset({429, 500, 502, 503, 504})

    def __init__(self, config: KnightSettings | None = None, max_attempts: int = 3) -> None:
        self._config = config or get_settings()
        self._max_attempts = max_attempts

    # --- Unauthenticated -----------------------------------------------------

    def handshake(self) -> dict[str, Any]:
        """
        Exchanges the client credential for a store token.

        The nonce makes a captured request body useless a second time: KNIGHT
        remembers it for the length of its window and refuses a replay.
        """
        payload = {
            "clientId": self._config.client_id,
            "clientSecret": self._config.client_secret,
            "environment": self._config.environment,
            "storeVersion": self._config.store_version,
            "runtime": _runtime_description(),
            "nonce": uuid.uuid4().hex,
        }

        return self._request("POST", "/api/v1/ingest/handshake", json=payload, authenticated=False)

    # --- Authenticated -------------------------------------------------------

    def heartbeat(
        self,
        status: str,
        dependencies: dict[str, Any] | None = None,
        features: list[str] | None = None,
        detail: str | None = None,
    ) -> dict[str, Any]:
        payload = {
            "environment": self._config.environment,
            "status": status,
            "storeVersion": self._config.store_version,
            "dependencies": dependencies or {},
            "features": features or [],
            "detail": detail,
        }

        return self._request("POST", "/api/v1/ingest/heartbeat", json=payload)

    def send_errors(self, events: list[dict[str, Any]], idempotency_key: str | None = None) -> dict[str, Any]:
        payload = {
            "environment": self._config.environment,
            "version": self._config.store_version,
            "events": events,
        }

        return self._request("POST", "/api/v1/ingest/errors", json=payload, idempotency_key=idempotency_key)

    def send_events(self, events: list[dict[str, Any]], idempotency_key: str | None = None) -> dict[str, Any]:
        payload = {"environment": self._config.environment, "events": events}

        return self._request("POST", "/api/v1/ingest/events", json=payload, idempotency_key=idempotency_key)

    def send_logs(self, entries: list[dict[str, Any]], idempotency_key: str | None = None) -> dict[str, Any]:
        payload = {
            "environment": self._config.environment,
            "version": self._config.store_version,
            "entries": entries,
        }

        return self._request("POST", "/api/v1/ingest/logs", json=payload, idempotency_key=idempotency_key)

    # --- The job channel -----------------------------------------------------
    #
    # Outbound only. The store asks for work; KNIGHT never connects inward. That
    # is what lets a store sit behind a firewall with no inbound port and still
    # receive features (docs/feature-delivery.md §7).

    def claim_job(self) -> dict[str, Any] | None:
        """
        Claims this store's next installation job, or returns None.

        None is the overwhelmingly common answer and is not an error: KNIGHT
        answers 204 when there is nothing queued, and the client turns that into
        an empty result rather than something the caller has to catch.
        """
        result = self._request("POST", "/api/v1/ingest/jobs/next")
        return result or None

    def report_step(
        self,
        job_id: str,
        step: str,
        status: str,
        output: str | None = None,
        error_code: str | None = None,
        duration_ms: int | None = None,
    ) -> None:
        """
        Reports one step's outcome.

        Safe to call twice for the same step. KNIGHT updates the step in place
        rather than appending, because an agent that completed a step and lost
        the reply will report it again, and a job that treated the repeat as a
        second execution would be a job that ran a migration twice.
        """
        self._request(
            "POST",
            f"/api/v1/ingest/jobs/{job_id}/steps",
            json={
                "step": step,
                "status": status,
                "output": output,
                "errorCode": error_code,
                "durationMilliseconds": duration_ms,
            },
        )

    def complete_job(
        self,
        job_id: str,
        succeeded: bool,
        failure_code: str | None = None,
        failure_message: str | None = None,
        rollback_outcome: str | None = None,
        installed_version: str | None = None,
        health: str | None = None,
    ) -> None:
        """Reports the final outcome, including how far any rollback got."""
        self._request(
            "POST",
            f"/api/v1/ingest/jobs/{job_id}/complete",
            json={
                "succeeded": succeeded,
                "failureCode": failure_code,
                "failureMessage": failure_message,
                "rollbackOutcome": rollback_outcome,
                "installedVersion": installed_version,
                "health": health,
            },
        )

    def fetch_entitlements(self) -> dict[str, Any]:
        return self._request("GET", "/api/v1/ingest/features")

    # --- Plumbing ------------------------------------------------------------

    def _request(
        self,
        method: str,
        path: str,
        json: dict[str, Any] | None = None,
        authenticated: bool = True,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        url = f"{self._config.base_url}{path}"
        headers = {"Accept": "application/json"}

        if idempotency_key:
            headers["Idempotency-Key"] = idempotency_key

        last_error: Exception | None = None

        for attempt in range(1, self._max_attempts + 1):
            if authenticated:
                from .auth import get_session

                headers["Authorization"] = f"Bearer {get_session().access_token}"

            try:
                response = requests.request(
                    method,
                    url,
                    json=json,
                    headers=headers,
                    timeout=self._config.timeout_seconds,
                )
            except requests.RequestException as exc:
                last_error = exc
                logger.debug("KNIGHT call %s %s failed on attempt %s: %s", method, path, attempt, exc)
                self._backoff(attempt)
                continue

            if response.status_code == 401 and authenticated:
                # The token expired or was minted before a credential rotation.
                # Drop it and let the next attempt handshake; a second 401 means
                # the credential itself is wrong.
                from .auth import forget_session

                forget_session()
                last_error = KnightRejected(401, "The store token was refused.")

                if attempt < self._max_attempts:
                    continue

                raise last_error

            if response.status_code in self.RETRYABLE_STATUS:
                last_error = KnightUnavailable(f"KNIGHT answered {response.status_code}.")
                self._backoff(attempt)
                continue

            if response.status_code >= 400:
                detail, code = _describe_problem(response)
                raise KnightRejected(response.status_code, detail, code)

            if not response.content:
                return {}

            try:
                return response.json()
            except ValueError as exc:
                raise KnightUnavailable("KNIGHT answered with a body that is not JSON.") from exc

        raise KnightUnavailable(f"KNIGHT could not be reached after {self._max_attempts} attempts.") from last_error

    def _backoff(self, attempt: int) -> None:
        if attempt >= self._max_attempts:
            return

        # Jittered, so a fleet of stores recovering from the same outage does not
        # arrive back in lockstep.
        time.sleep(min(2 ** (attempt - 1) * 0.25 + random.random() * 0.25, 5.0))


def _describe_problem(response: requests.Response) -> tuple[str, str]:
    """Reads a ProblemDetails body if there is one, without assuming there is."""
    try:
        body = response.json()
    except ValueError:
        return response.text[:200] or response.reason, ""

    if not isinstance(body, dict):
        return response.reason, ""

    return str(body.get("detail") or body.get("title") or response.reason), str(body.get("errorCode", ""))


def _runtime_description() -> str:
    import platform

    import django

    return f"Python {platform.python_version()} / Django {django.get_version()}"
