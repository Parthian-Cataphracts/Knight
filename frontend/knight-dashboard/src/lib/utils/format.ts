// The UI is English-only (docs/risks.md §3.6), so numbers and dates are
// formatted in en-US regardless of what the document's lang attribute says.
const localeTag = (_locale: string) => "en-US";

export function formatNumber(value: number, locale = document.documentElement.lang): string {
  return new Intl.NumberFormat(localeTag(locale)).format(value);
}

export function formatPercent(value: number, locale = document.documentElement.lang): string {
  return new Intl.NumberFormat(localeTag(locale), {
    style: "percent",
    maximumFractionDigits: 1,
  }).format(value / 100);
}

export function formatDateTime(iso: string, locale = document.documentElement.lang): string {
  return new Intl.DateTimeFormat(localeTag(locale), {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(iso));
}

/**
 * Accepts null because plenty of timestamps in the API are genuinely absent —
 * a report with no data behind it yet, an installation that has never
 * transitioned. Passing one of those through produced "20,685 days ago", the
 * epoch rendered as though it were a fact, which is worse than saying nothing.
 */
export function formatRelative(
  iso: string | null | undefined,
  locale = document.documentElement.lang,
): string {
  if (!iso) {
    return "—";
  }

  const at = new Date(iso).getTime();

  if (Number.isNaN(at)) {
    return "—";
  }

  const rtf = new Intl.RelativeTimeFormat(localeTag(locale), { numeric: "auto" });
  const diffMs = at - Date.now();
  const units: [Intl.RelativeTimeFormatUnit, number][] = [
    ["day", 86_400_000],
    ["hour", 3_600_000],
    ["minute", 60_000],
    ["second", 1000],
  ];
  for (const [unit, ms] of units) {
    if (Math.abs(diffMs) >= ms || unit === "second") {
      return rtf.format(Math.round(diffMs / ms), unit);
    }
  }
  return "";
}

/**
 * Keeps only the digits in `value`, accepting Persian (۰-۹) and Arabic-Indic
 * (٠-٩) digits as well as ASCII ones and returning ASCII throughout.
 *
 * The UI is English, but an operator on a Persian or Arabic keyboard still types
 * U+06F0-U+06F9. Those are not matched by `\d`, which is ASCII-only: filtering
 * with `/\D/g` alone discards every keystroke and the field cannot be filled at
 * all. The API wants ASCII, so this normalises rather than merely permitting.
 */
export function digitsOnly(value: string): string {
  return value.replace(/[^\d\u06F0-\u06F9\u0660-\u0669]/g, "").replace(
    /[\u06F0-\u06F9\u0660-\u0669]/g,
    (digit) => {
      const code = digit.charCodeAt(0);
      const base = code >= 0x06f0 ? 0x06f0 : 0x0660;
      return String(code - base);
    },
  );
}
