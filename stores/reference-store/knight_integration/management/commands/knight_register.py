"""
`manage.py knight_register` — the handshake, run by hand.

This is step 3 of the store lifecycle (docs/store-integration.md §2): an
operator has registered the store in KNIGHT, issued a credential, and put the
secret in this store's environment. Running this proves the credential works
and moves the store out of NotRegistered.

It is deliberately a command rather than something that happens at startup. A
store must boot whether or not KNIGHT is reachable, and an operator running this
gets a clear answer — including the exact reason it failed — instead of a line
in a log nobody was watching.
"""

from __future__ import annotations

from django.core.management.base import BaseCommand, CommandError

from ...auth import forget_session, get_session
from ...client import KnightRejected, KnightUnavailable
from ...conf import KnightConfigurationError, get_settings


class Command(BaseCommand):
    help = "Performs the KNIGHT handshake and reports the resulting integration status."

    def add_arguments(self, parser) -> None:
        parser.add_argument(
            "--force",
            action="store_true",
            help="Ignore any cached token and handshake again.",
        )

    def handle(self, *args, **options) -> None:
        config = get_settings()

        try:
            config.require_credentials()
        except KnightConfigurationError as exc:
            raise CommandError(str(exc)) from exc

        if options["force"]:
            forget_session()

        self.stdout.write(f"Registering with KNIGHT at {config.base_url} as {config.client_id}...")

        try:
            session = get_session(force_refresh=options["force"])
        except KnightRejected as exc:
            raise CommandError(
                f"KNIGHT refused the credentials ({exc.status_code}). Check the client id, the secret, and that "
                f"KNIGHT_ENVIRONMENT ('{config.environment}') matches how the store is registered."
            ) from exc
        except KnightUnavailable as exc:
            raise CommandError(f"KNIGHT could not be reached: {exc}") from exc

        self.stdout.write(self.style.SUCCESS(f"Registered. Store {session.store_id}, environment {session.environment}."))
        self.stdout.write(f"Integration status: {session.integration_status}")
        self.stdout.write(f"Token valid for {int(session.expires_at - __import__('time').time())} seconds.")

        if session.domain_verification_outstanding:
            self.stdout.write("")
            self.stdout.write(
                self.style.WARNING(
                    "This store is Pending, not Connected: its primary domain has not been proven yet."
                )
            )

            if session.domain_verification_token:
                self.stdout.write(
                    "Set KNIGHT_DOMAIN_VERIFICATION_TOKEN to the value below, restart the store, then verify "
                    "the domain from the KNIGHT dashboard:"
                )
                self.stdout.write("")
                self.stdout.write(f"    KNIGHT_DOMAIN_VERIFICATION_TOKEN={session.domain_verification_token}")
            else:
                self.stdout.write("Ask an operator to start domain verification for this store in KNIGHT.")

        self.stdout.write("")
        self.stdout.write(f"Heartbeat every {session.heartbeat_seconds}s, entitlement refresh every {session.feature_refresh_seconds}s.")
