"""
`manage.py knight_selftest` — prove the whole integration works, end to end.

Runs everything a live store does, in order, and says which step failed if one
does. This is the command an operator runs after configuring a store, and the
one to run first when something is wrong: it separates "the credential is
rejected" from "the domain is not verified" from "the database is down", which
is most of the diagnosis.

Nothing here is destructive. The error it reports is one it raises itself, and
it is labelled as a self-test so nobody mistakes it for a real incident.
"""

from __future__ import annotations

import json

from django.core.management.base import BaseCommand, CommandError

from ...auth import get_session
from ...client import KnightClient, KnightRejected, KnightUnavailable
from ...conf import KnightConfigurationError, get_settings
from ...errors.middleware import build_event
from ...features import current, installed_features, refresh
from ...health import checks


class SelfTestError(RuntimeError):
    pass


class Command(BaseCommand):
    help = "Exercises the whole KNIGHT integration and reports what works."

    def add_arguments(self, parser) -> None:
        parser.add_argument(
            "--skip-error",
            action="store_true",
            help="Do not send a synthetic error event.",
        )

    def handle(self, *args, **options) -> None:
        failures: list[str] = []

        for name, step in self._steps(skip_error=options["skip_error"]):
            try:
                detail = step()
                self.stdout.write(self.style.SUCCESS(f"  ok    {name}") + (f" — {detail}" if detail else ""))
            except Exception as exc:  # noqa: BLE001 - every failure is a result to report
                failures.append(name)
                self.stdout.write(self.style.ERROR(f"  FAIL  {name} — {exc}"))

        self.stdout.write("")

        if failures:
            raise CommandError(f"{len(failures)} step(s) failed: {', '.join(failures)}")

        self.stdout.write(self.style.SUCCESS("Every step passed."))

    def _steps(self, skip_error: bool):
        yield "configuration", self._check_configuration
        yield "dependencies", self._check_dependencies
        yield "handshake", self._check_handshake
        yield "heartbeat", self._check_heartbeat
        yield "entitlements", self._check_entitlements

        if not skip_error:
            yield "error reporting", self._check_error_reporting

    def _check_configuration(self) -> str:
        config = get_settings()

        try:
            config.require_credentials()
        except KnightConfigurationError as exc:
            raise SelfTestError(str(exc)) from exc

        return f"{config.base_url} as {config.client_id} ({config.environment})"

    def _check_dependencies(self) -> str:
        status, dependencies = checks.run_all()

        if status != checks.HEALTHY:
            raise SelfTestError(f"the store reports {status}: {json.dumps(dependencies)}")

        return ", ".join(f"{name} {result.get('latencyMs', 0)}ms" for name, result in dependencies.items())

    def _check_handshake(self) -> str:
        try:
            session = get_session(force_refresh=True)
        except (KnightRejected, KnightUnavailable) as exc:
            raise SelfTestError(str(exc)) from exc

        if session.domain_verification_outstanding:
            return f"{session.integration_status} — the domain is not verified yet"

        return session.integration_status

    def _check_heartbeat(self) -> str:
        status, dependencies = checks.run_all()

        try:
            receipt = KnightClient().heartbeat(
                status=status,
                dependencies=dependencies,
                features=list(installed_features()),
                detail="knight_selftest",
            )
        except (KnightRejected, KnightUnavailable) as exc:
            raise SelfTestError(str(exc)) from exc

        return f"KNIGHT now has this store as {receipt.get('integrationStatus')}"

    def _check_entitlements(self) -> str:
        try:
            entitlements = refresh()
        except ValueError as exc:
            raise SelfTestError(f"{exc} Run `knight_register --force` and try again.") from exc
        except (KnightRejected, KnightUnavailable) as exc:
            # Not fatal to the store, but it is a failed step: the whole point of
            # the self-test is to say so.
            raise SelfTestError(f"{exc} (the store would fall back to '{current().source}')") from exc

        return ", ".join(sorted(entitlements.slugs)) or "no features entitled"

    def _check_error_reporting(self) -> str:
        try:
            raise SelfTestError("Synthetic error raised by knight_selftest. Nothing is wrong.")
        except SelfTestError as exc:
            event = build_event(None, exc, status_code=0)

        try:
            receipt = KnightClient().send_errors([event])
        except (KnightRejected, KnightUnavailable) as exc:
            raise SelfTestError(str(exc)) from exc

        return f"{receipt.get('accepted', 0)} accepted, {receipt.get('rejected', 0)} rejected"
