"""
Who is allowed to talk to this service, and what has already been said.

Two tables and no more. A service that grew a user model, a permission model and
a session table would have become a second place identity is decided, and the
whole point of the architecture is that identity is decided in one place — the
store — and asserted to this service under a signature
(``docs/adr/0033-api-driven-features.md``).
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

    The secret is the one the store was configured with, and the pairing is
    per-store on purpose. One shared secret across a fleet means the first store
    to leak it can impersonate every other, and that would be discovered exactly
    once.
    """

    #: The store's id in KNIGHT. The stable name across all three systems.
    store_id = models.UUIDField(unique=True)

    slug = models.SlugField(max_length=120)

    #: The base URL of the store, recorded so a person reading this table knows
    #: which shop a row is. Never used to call the store: this service is
    #: strictly answered-only, and something that called back into a store would
    #: need its own authentication story.
    base_url = models.URLField(blank=True, default="")

    #: The shared secret this store signs with. Never logged, never returned by
    #: any endpoint, and never compared with `==` — see `signing.verify`.
    secret = models.CharField(max_length=200)

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

    store = models.ForeignKey(Store, on_delete=models.CASCADE, related_name="nonces")
    nonce = models.CharField(max_length=100)
    seen_at = models.DateTimeField(default=timezone.now, db_index=True)

    class Meta:
        db_table = "knight_seen_nonce"
        constraints = [
            # The guarantee, in the database rather than in a check-then-insert.
            # Two concurrent replays of the same captured request would both
            # find nothing and both proceed, and one of them would win.
            models.UniqueConstraint(fields=["store", "nonce"], name="knight_nonce_once"),
        ]

    def __str__(self) -> str:
        return f"{self.store.slug}:{self.nonce}"
