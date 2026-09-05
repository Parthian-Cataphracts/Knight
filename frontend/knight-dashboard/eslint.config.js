import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";

/**
 * Flat config. TypeScript, the rules of hooks, and one house rule of our own:
 * physical CSS utilities are refused in favour of their logical counterparts, so
 * a component reads and lays out the same whichever way the document flows.
 */
export default tseslint.config(
  {
    ignores: ["dist", "src/lib/api/schema.ts", "src/lib/api/openapi.json"],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ["**/*.{ts,tsx}"],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
    },
    plugins: {
      "react-hooks": reactHooks,
      "react-refresh": reactRefresh,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      "react-refresh/only-export-components": ["warn", { allowConstantExport: true }],

      // The rules of hooks and exhaustive-deps stay on. `set-state-in-effect` is
      // off deliberately: the one pattern it flags here is a drawer resetting its
      // own contained form state when it opens on a different subject, which is a
      // supported use of an effect (React's own "adjust state when a prop
      // changes"), not the cascading-render mistake the rule targets.
      "react-hooks/set-state-in-effect": "off",

      // Unused code is a smell the type-checker does not flag; a leading
      // underscore is the opt-out for a deliberately-unused binding.
      "@typescript-eslint/no-unused-vars": ["error", { argsIgnorePattern: "^_", varsIgnorePattern: "^_" }],

      // Physical CSS utilities in a className. The codebase lays out with logical
      // properties (ms-/me-, ps-/pe-, start-/end-, text-start/text-end) so a
      // physical ml-/pr-/text-left is both inconsistent and wrong the moment the
      // document is not left-to-right. Caught here rather than in review.
      "no-restricted-syntax": [
        "error",
        {
          selector:
            "Literal[value=/(^|\\s)-?(ml|mr|pl|pr|left|right|border-l|border-r|rounded-l|rounded-r)-/]",
          message:
            "Use a logical utility (ms-/me-, ps-/pe-, start-/end-, border-s/border-e, rounded-s/rounded-e) rather than a physical one (ml-/mr-, pl-/pr-, left-/right-).",
        },
        {
          selector: "Literal[value=/(^|\\s)text-(left|right)(\\s|$)/]",
          message: "Use text-start / text-end rather than text-left / text-right.",
        },
      ],
    },
  },
  {
    // Tests and config may use Node globals and speak a little more loosely.
    files: ["**/*.test.{ts,tsx}", "**/*.config.{js,ts}", "vite.config.ts"],
    languageOptions: {
      globals: { ...globals.node, ...globals.browser },
    },
    rules: {
      "no-restricted-syntax": "off",
    },
  },
);
