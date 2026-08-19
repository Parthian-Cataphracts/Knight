"""
Lifecycle events this store reports to KNIGHT.

Deployments and backups, not business facts: KNIGHT is a control plane and has
no business knowing that an order was placed (docs/README.md rule 1).
"""

from .reporter import report_deployment, report_event

__all__ = ["report_deployment", "report_event"]
