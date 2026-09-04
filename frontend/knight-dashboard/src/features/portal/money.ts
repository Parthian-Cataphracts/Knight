/** A shared currency formatter for the portal's prices. */
export function formatMoney(amount: number, currency: string, locale = document.documentElement.lang): string {
  try {
    return new Intl.NumberFormat(locale, { style: "currency", currency }).format(amount);
  } catch {
    return `${amount} ${currency}`;
  }
}
