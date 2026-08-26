"""
The health check KNIGHT runs after installing this feature.

An install that finishes and then does not work is a failed install. This runs
the two things most likely to be broken after installing this one, rather than
returning True:

- **the derived arithmetic**, because a stock level here is a sum over a table
  rather than a column, and an aggregate that raises is a Feature whose every
  page is a 500;
- **the trigram lookup**, because it needs the `pg_trgm` extension the manifest
  declares, and the install where that is missing is precisely the one that has
  to fail here. The package would be fine, the migrations would have applied, and
  the first member of staff to type into the stock picker would get an error
  (docs/adr/0031-database-extensions-are-declared-not-migrated.md).
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when the ledger can be summed and the fuzzy lookups can be run."""
    try:
        from . import services
        from .models import StockItem, StockMovement

        StockItem.objects.exists()
        StockMovement.objects.exists()

        # Exercises the aggregate and the reservation join without needing any
        # data: a store with nothing in it must still pass this.
        services.levels()

        # Exercises pg_trgm. A query matching nothing is fine; one that raises
        # means the extension is not there.
        services.find_items("knight-health-check-sku-that-matches-nothing")
        services.find_suppliers("knight-health-check-supplier-that-matches-nothing")
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Advanced inventory health check failed.")
        return False

    return True
