"""
Who is allowed to talk to this service, and what has already been said.

Three tables and no more. A service that grew a user model, a permission model
and a session table would have become a second place identity is decided, and
the whole point of the architecture is that identity is decided in one place —
the store — and asserted to this service under a signature
(``docs/adr/0033-api-driven-features.md``).

The third table is what phase 24 added: a store's shared secret is a **row with
a lifetime** rather than a column, because a secret that cannot be changed
without an outage is a secret nobody changes.
"""

from __future__ import annotations

from django.db import models
from django.utils import timezone


class Store(models.Model):
    """
    One store this service will answer.

    Created by an operator when a customer is entitled to the Feature, not by a
    store presenting itself: a service that registered whoever called it would
    have no notion of who is allowed to call it at all.

    The secrets it signs with are its own, and the pairing is per-store on
    purpose. One shared secret across a fleet means the first store to leak it
    can impersonate every other, and that would be discovered exactly once.

    There is no `secret` column. A store has a **set** of currently usable
    secrets (`StoreSecret`), because rotating one that had to be the only one
    would refuse every request already in flight.
    """

    #: The store's id in KNIGHT. The stable name across all three systems.
    store_id = models.UUIDField(unique=True)

    slug = models.SlugField(max_length=120)

    #: The base URL of the store, recorded so a person reading this table knows
    #: which shop a row is. Never used to call the store: this service is
    #: strictly answered-only, and something that called back into a store would
    #: need its own authentication story.
    base_url = models.URLField(blank=True, default="")

    #: This store's own settings for this Feature — provider, currency, retry
    #: policy. Per store, because one deployment serves every shop: in 1.x these
    #: arrived in a file the installer wrote beside a per-store installation,
    #: and here they are a column.
    settings = models.JSONField(default=dict, blank=True)

    #: Payment credentials, per store. One provider key shared across a fleet
    #: would mean every merchant's charges landing in one account, which is not
    #: a bug anybody recovers from quietly.
    #:
    #: Never logged, never returned by an endpoint, never in an error message.
    secrets = models.JSONField(default=dict, blank=True)

    #: Off, and the store keeps existing. An entitlement that lapsed must stop
    #: this service answering **as well as** stop the store forwarding: relying
    #: on the store alone would mean a store with a stale registration could
    #: still reach a Feature its customer no longer pays for.
    enabled = models.BooleanField(default=True)

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        db_table = "knight_store"
        ordering = ("slug",)

    def __str__(self) -> str:
        return f"{self.slug} ({self.store_id})"

    # --- Secrets -------------------------------------------------------------

    def usable_secrets(self, now=None) -> list["StoreSecret"]:
        """
        Every secret a request may currently be signed with, newest first.

        Plural, and that is the whole of what makes rotation survivable. A
        service that accepted only the newest would refuse every request already
        in flight at the moment of the change, so a rotation would be an outage
        and nobody would ever perform one.

        Newest first because that is what almost every request will match, and
        each miss costs an HMAC.
        """
        now = now or timezone.now()

        return list(
            self.signing_secrets.filter(revoked_at__isnull=True, valid_from__lte=now)
            .filter(models.Q(expires_at__isnull=True) | models.Q(expires_at__gt=now))
            .order_by("-valid_from", "-id")
        )

    def rotate_to(self, secret: str, *, overlap_seconds: int = 0, issued_by: str = "", now=None):
        """
        Issues a new secret and starts the old ones expiring.

        `overlap_seconds` is how long the previous ones keep working. Zero is a
        revocation dressed as a rotation, and it is allowed on purpose — it is
        what a leak needs — but it is not the default, because the default
        should be the one that drops nobody's request.

        Idempotent on the value: rotating to a secret this store already signs
        with returns that row rather than issuing a twin, because KNIGHT
        retrying a delivery it is not sure arrived is the normal case.
        """
        from datetime import timedelta

        now = now or timezone.now()
        existing = self.signing_secrets.filter(secret=secret, revoked_at__isnull=True).first()

        if existing is not None:
            return existing

        cutoff = now + timedelta(seconds=max(0, int(overlap_seconds)))

        # Downwards only. A rotation must never extend the life of a secret that
        # was already on its way out.
        for previous in self.usable_secrets(now):
            if previous.expires_at is None or previous.expires_at > cutoff:
                previous.expires_at = cutoff
                previous.save(update_fields=["expires_at"])

        return self.signing_secrets.create(
            secret=secret, valid_from=now, issued_by=str(issued_by)[:200]
        )

    def revoke_secrets(self, now=None) -> int:
        """
        Ends every secret this store has, at once.

        What a leak needs, and what a withdrawn entitlement needs. Separate from
        `enabled` because the two answer different questions — disabled is "this
        store may not use this service", revoked is "this key is not a key any
        more" — and an incident usually wants both.
        """
        return self.signing_secrets.filter(revoked_at__isnull=True).update(
            revoked_at=now or timezone.now()
        )


class StoreSecret(models.Model):
    """
    One shared secret, with a beginning and an end.

    **A row rather than a column**, which is the change the rest of phase 24 is
    built on. Two secrets are accepted at once for the length of one window, so
    a request signed a second before a rotation still verifies a second after
    it, and changing a secret stops being something that needs a maintenance
    window to do safely.

    Never logged, never returned by any endpoint, and never compared with `==`
    (`signing.verify`).
    """

    store = models.ForeignKey(Store, on_delete=models.CASCADE, related_name="signing_secrets")

    secret = models.CharField(max_length=200)

    valid_from = models.DateTimeField(default=timezone.now)

    #: When this secret stops being accepted. Null while it is the current one:
    #: a secret with no successor has no reason to expire, and inventing a
    #: lifetime for it would mean a store going dark on a date nobody chose.
    expires_at = models.DateTimeField(null=True, blank=True)

    #: Set when a secret is ended rather than aged out — a leak, or an
    #: entitlement withdrawn. Kept rather than deleted: "when did this key stop
    #: working" is the first question of any incident.
    revoked_at = models.DateTimeField(null=True, blank=True)

    #: Who issued it. `knight` for the control plane, or an operator's note.
    issued_by = models.CharField(max_length=200, blank=True, default="")

    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_store_secret"
        ordering = ("-valid_from", "-id")
        indexes = [
            models.Index(fields=["store", "expires_at"], name="knight_secret_window"),
        ]
        constraints = [
            # One row per value per store. KNIGHT retries a delivery it is not
            # sure arrived, and the second attempt must find the secret it
            # issued rather than add another one that also works.
            models.UniqueConstraint(fields=["store", "secret"], name="knight_secret_once"),
        ]

    def is_usable(self, now=None) -> bool:
        now = now or timezone.now()

        return (
            self.revoked_at is None
            and self.valid_from <= now
            and (self.expires_at is None or self.expires_at > now)
        )

    def __str__(self) -> str:
        # Never the value. This lands in admin pages, logs and tracebacks.
        return f"{self.store.slug} secret from {self.valid_from:%Y-%m-%d %H:%M}"


class SeenNonce(models.Model):
    """
    A nonce this service has already accepted, and will not accept again.

    The third leg of the signature check. The HMAC proves the message was not
    altered and the timestamp proves it is recent; without this, a request
    captured thirty seconds ago is still perfectly valid and can be sent a
    hundred more times — which for a service that cancels subscriptions is not a
    theoretical problem.

    Scoped to the store, because two stores generating the same random value is
    not one of them replaying the other.
    """

    #: Null for a request from KNIGHT itself, which is nobody's store. The
    #: control plane needs the same replay protection and belongs to no tenant,
    #: and a placeholder store row would be a fake tenant in the table that
    #: means tenancy.
    store = models.ForeignKey(
        Store, on_delete=models.CASCADE, related_name="nonces", null=True, blank=True
    )
    nonce = models.CharField(max_length=100)
    seen_at = models.DateTimeField(default=timezone.now, db_index=True)

    class Meta:
        db_table = "knight_seen_nonce"
        constraints = [
            # The guarantee, in the database rather than in a check-then-insert.
            # Two concurrent replays of the same captured request would both
            # find nothing and both proceed, and one of them would win.
            models.UniqueConstraint(fields=["store", "nonce"], name="knight_nonce_once"),
            # And the same guarantee for KNIGHT's own requests, which have no
            # store. It needs its own constraint because PostgreSQL considers
            # two NULLs distinct — the constraint above would have let the
            # control plane's requests be replayed freely, which is the one
            # caller that can rotate a secret.
            models.UniqueConstraint(
                fields=["nonce"],
                condition=models.Q(store__isnull=True),
                name="knight_control_nonce_once",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.store.slug}:{self.nonce}"
