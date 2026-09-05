import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import en from "./en.json";

// The dashboard is English-only (docs/risks.md §3.6). The locale scaffolding is
// kept — a single-entry list and a direction map — so a second language can be
// added later without touching every call site, but there is nothing to switch
// to today and no switcher offering one.
export const SUPPORTED_LOCALES = ["en"] as const;
export type Locale = (typeof SUPPORTED_LOCALES)[number];

export const DIRECTION: Record<Locale, "rtl" | "ltr"> = { en: "ltr" };

void i18n.use(initReactI18next).init({
  resources: { en: { translation: en } },
  lng: "en",
  fallbackLng: "en",
  interpolation: { escapeValue: false },
});

export default i18n;
