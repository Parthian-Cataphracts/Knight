"""
Registering a store with this service.

An operator action, not something a store can do for itself. A service that
registered whoever called it would have no notion of who is allowed to call it
at all, which is the whole of what `Store` is for.

    python manage.py knight_store add --slug camden-coffee --store-id <uuid> --secret <shared secret>
    python manage.py knight_store list
    python manage.py knight_store disable --slug camden-coffee
"""

from __future__ import annotations

import json

from django.core.management.base import BaseCommand, CommandError

from knightlink.models import Store


class Command(BaseCommand):
    help = "Register, list or disable a store this service will answer."

    def add_arguments(self, parser) -> None:
        parser.add_argument("action", choices=["add", "list", "disable", "enable"])
        parser.add_argument("--slug")
        parser.add_argument("--store-id")
        parser.add_argument("--secret")
        parser.add_argument("--base-url", default="")
        parser.add_argument(
            "--settings-json",
            default="",
            help="This store's configuration, e.g. '{\"provider\": \"manual\"}'.",
        )

    def handle(self, *args, **options) -> None:
        action = options["action"]

        if action == "list":
            self._list()
            return

        slug = options.get("slug")

        if not slug:
            raise CommandError("--slug is required.")

        if action in {"disable", "enable"}:
            updated = Store.objects.filter(slug=slug).update(enabled=action == "enable")

            if not updated:
                raise CommandError(f"No store is registered as '{slug}'.")

            self.stdout.write(f"{slug} is now {'enabled' if action == 'enable' else 'disabled'}.")
            return

        if not options.get("store_id") or not options.get("secret"):
            raise CommandError("--store-id and --secret are required to add a store.")

        try:
            configuration = json.loads(options["settings_json"] or "{}")
        except ValueError as exc:
            raise CommandError(f"--settings-json is not valid JSON: {exc}") from exc

        store, created = Store.objects.update_or_create(
            store_id=options["store_id"],
            defaults={
                "slug": slug,
                "secret": options["secret"],
                "base_url": options["base_url"],
                "settings": configuration,
                "enabled": True,
            },
        )

        # The secret is never echoed back, here or anywhere. An operator who
        # needs it again rotates it rather than reads it.
        self.stdout.write(
            self.style.SUCCESS(f"{'Registered' if created else 'Updated'} {store.slug} ({store.store_id}).")
        )

    def _list(self) -> None:
        found = Store.objects.all()

        if not found.exists():
            self.stdout.write("No stores are registered. This service will answer nobody.")
            return

        for store in found:
            state = "enabled" if store.enabled else "DISABLED"
            self.stdout.write(f"  {store.slug:<28} {store.store_id}  {state}")
