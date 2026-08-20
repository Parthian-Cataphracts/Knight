const localeTag = (locale: string) => (locale === "fa" ? "fa-IR" : "en-US");

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
