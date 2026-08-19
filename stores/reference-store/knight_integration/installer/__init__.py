"""
Installing feature packages into this store.

The store half of docs/feature-delivery.md §7. Nothing here is imported by
business code, and nothing here imports it.
"""

from .runner import JobOutcome, JobRunner
from .state import InstallationRegistry, InstalledFeature, get_registry
from .verify import ArtifactRejected, verify_artifact

__all__ = [
    "ArtifactRejected",
    "InstallationRegistry",
    "InstalledFeature",
    "JobOutcome",
    "JobRunner",
    "get_registry",
    "verify_artifact",
]
