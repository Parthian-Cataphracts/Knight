/** A shared currency formatter for the portal's prices. */
export function formatMoney(amount: number, currency: string, locale = document.documentElement.lang): string {
  // Toman is not an ISO currency, so Intl cannot render it — format the number
  // with the locale's grouping and append the unit ourselves. The catalogue marks
  // Toman prices with one of these codes.
  const code = currency?.toUpperCase();
  if (code === "IRT" || code === "TOMAN" || code === "TMN") {
    try {
      return `${new Intl.NumberFormat(locale, { maximumFractionDigits: 0 }).format(amount)} تومان`;
    } catch {
      return `${amount} تومان`;
    }
  }

  try {
    return new Intl.NumberFormat(locale, { style: "currency", currency }).format(amount);
  } catch {
    return `${amount} ${currency}`;
  }
}
