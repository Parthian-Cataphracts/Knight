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

export function formatRelative(iso: string, locale = document.documentElement.lang): string {
  const rtf = new Intl.RelativeTimeFormat(localeTag(locale), { numeric: "auto" });
  const diffMs = new Date(iso).getTime() - Date.now();
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
