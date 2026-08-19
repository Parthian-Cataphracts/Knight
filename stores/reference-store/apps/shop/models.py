"""
The store's own business data.

A deliberately small domain — this is a reference store, not a product — but a
real one: its own tables, in its own database, that KNIGHT never sees and never
connects to (docs/README.md rule 3). Phase 8 replaces this with the ported
catalog and ordering domain.
"""

from django.db import models


class Product(models.Model):
    name = models.CharField(max_length=200)
    slug = models.SlugField(unique=True)
    price = models.DecimalField(max_digits=10, decimal_places=2)
    is_available = models.BooleanField(default=True)
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        ordering = ("name",)

    def __str__(self) -> str:
        return self.name


class LoyaltyAccount(models.Model):
    """
    Belongs to the loyalty capability, which is sold separately.

    The model exists whether or not the customer is entitled to it — data
    outlives entitlement, and losing an entitlement disables a capability rather
    than deleting what it recorded
    ([`adr/0016`](../../../../docs/adr/0016-feature-migration-and-removal-policy.md)).
    """

    email = models.EmailField(unique=True)
    points = models.PositiveIntegerField(default=0)

    def __str__(self) -> str:
        return f"{self.email}: {self.points} points"
