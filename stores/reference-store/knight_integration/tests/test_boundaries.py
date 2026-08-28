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


def is_a_test(path: Path) -> bool:
    """
    Whether this file is a test rather than business code.

    Tests are held to a different rule below, and the distinction is real rather
    than a loophole. The rule is about what the store's **running code** depends
    on: a business module that imported the event bus would still be importing
    it in production, and would break the day that bus changed shape.

    A test arranging a scenario is coupled to internals by definition — that is
    what mocking is — and the alternative is worse. The subscription-order tests
    patch `external_features` and `requests.request` so the command runs through
    the real signing path and can assert that the request was signed and
    asserted the store itself. Forcing them to stub the façade instead would
    have deleted that coverage to satisfy a rule about something else.
    """
    return path.name.startswith("test_") or "tests" in path.parts


class LayeringTests(SimpleTestCase):
    def test_business_code_only_touches_the_feature_facade(self):
        offences: list[str] = []

        for path in BUSINESS_ROOT.rglob("*.py"):
            if is_a_test(path):
                continue

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

    def test_the_rule_still_bites_on_real_business_code(self):
        """
        The exemption for tests is for tests, and only for tests.

        Without this, `is_a_test` could be loosened until it matched
        everything and the suite would stay green while the boundary
        quietly disappeared.
        """
        offender = BUSINESS_ROOT / "orders" / "models.py"

        self.assertTrue(offender.exists(), "the file this asserts against has moved")
        self.assertFalse(is_a_test(offender))

    def test_the_facade_is_what_business_code_actually_uses(self):
        """
        The other half of the rule: not only "nothing deeper", but "this".

        A boundary nobody crosses because nobody uses it is not a boundary.
        The facade grew three verbs in phase 23 - announce, ask and
        serves_as_service - because business code needed them and would
        otherwise have reached past it.
        """
        from knight_integration import features

        for verb in ("is_enabled", "is_available", "announce", "ask", "serves_as_service"):
            self.assertTrue(hasattr(features, verb), f"the facade lost {verb}")
