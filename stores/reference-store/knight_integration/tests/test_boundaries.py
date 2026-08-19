"""
The layering rule, enforced rather than asserted in a document.

Business apps never import the integration layer's internals, and the
integration layer never imports a business model. Both halves of that rule are
easy to break by accident and impossible to notice by reading, so they are
checked here (docs/store-integration.md §1).
"""

from __future__ import annotations

import ast
from pathlib import Path

from django.test import SimpleTestCase

STORE_ROOT = Path(__file__).resolve().parents[2]
BUSINESS_ROOT = STORE_ROOT / "apps"
INTEGRATION_ROOT = STORE_ROOT / "knight_integration"

#: The one part of the integration layer business code may import.
ALLOWED_FROM_BUSINESS = ("knight_integration.features",)


def imported_modules(path: Path) -> set[str]:
    tree = ast.parse(path.read_text(encoding="utf-8"))
    names: set[str] = set()

    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            names.update(alias.name for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module and node.level == 0:
            names.add(node.module)

    return names


class LayeringTests(SimpleTestCase):
    def test_business_code_only_touches_the_feature_facade(self):
        offences: list[str] = []

        for path in BUSINESS_ROOT.rglob("*.py"):
            for module in imported_modules(path):
                if not module.startswith("knight_integration"):
                    continue

                if module not in ALLOWED_FROM_BUSINESS:
                    offences.append(f"{path.relative_to(STORE_ROOT)} imports {module}")

        self.assertEqual(
            [],
            offences,
            "Business code may only ask about features through knight_integration.features. "
            "Anything deeper couples the store's domain to how KNIGHT happens to work today:\n"
            + "\n".join(offences),
        )

    def test_the_integration_layer_never_imports_a_business_model(self):
        offences: list[str] = []

        for path in INTEGRATION_ROOT.rglob("*.py"):
            if "tests" in path.parts:
                continue

            for module in imported_modules(path):
                if module.startswith("apps"):
                    offences.append(f"{path.relative_to(STORE_ROOT)} imports {module}")

        self.assertEqual(
            [],
            offences,
            "The integration layer must not know what this store sells. Importing a business "
            "model here is how the layer becomes a place to put business rules:\n" + "\n".join(offences),
        )
