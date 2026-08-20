import type { TFunction } from "i18next";

/**
 * Renders a customer's plan for display.
 *
 * Two cases the translation table alone gets wrong:
 *
 * - A customer with no subscription has no plan key at all. Looking that up
 *   produced the literal string "planKey.null" on the customers list, which is
 *   worse than useless: it reads as a bug to the operator and hides the fact
 *   that the customer simply has not been sold anything yet.
 * - Plans are created by operators in the dashboard, so their keys cannot all be
 *   known to a translation file shipped with the build. An unknown key falls
 *   back to itself rather than to a missing-key placeholder.
 */
export function planLabel(t: TFunction, planKey: string | null | undefined): string {
  if (!planKey) {
    return t("customers.noPlan");
  }

  return t(`planKey.${planKey}`, { defaultValue: planKey });
}
