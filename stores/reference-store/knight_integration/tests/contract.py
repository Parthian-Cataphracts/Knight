"""
Loading the shared KNIGHT ↔ Store contract.

The schema and the worked examples live in the KNIGHT repository's docs/, not in
this package, so that both sides validate against the same bytes. Located by
walking up from this file rather than by a fixed relative path, which would
break the moment the store is vendored somewhere else.
"""

from __future__ import annotations

import json
from functools import lru_cache
from pathlib import Path
from typing import Any

SCHEMA_RELATIVE = Path("docs/contracts/store-integration.schema.json")
SAMPLES_RELATIVE = Path("docs/contracts/store-integration.samples.json")


class ContractNotFound(RuntimeError):
    pass


def _locate(relative: Path) -> Path:
    for parent in Path(__file__).resolve().parents:
        candidate = parent / relative
        if candidate.exists():
            return candidate

    raise ContractNotFound(
        f"Could not find {relative} above {__file__}. The reference store is tested against the "
        "contract in the KNIGHT repository; check out both together."
    )


@lru_cache(maxsize=1)
def schema() -> dict[str, Any]:
    return json.loads(_locate(SCHEMA_RELATIVE).read_text(encoding="utf-8"))


@lru_cache(maxsize=1)
def samples() -> dict[str, Any]:
    return json.loads(_locate(SAMPLES_RELATIVE).read_text(encoding="utf-8"))


def definition(name: str) -> dict[str, Any]:
    """One definition as a standalone schema, carrying its siblings so $refs resolve."""
    contract = schema()

    return {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$ref": f"#/$defs/{name}",
        "$defs": contract["$defs"],
    }


def assert_matches(name: str, payload: Any) -> None:
    import jsonschema

    jsonschema.validate(instance=payload, schema=definition(name))
