import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import en from "./en.json";
import fa from "./fa.json";

// Two languages: English and Persian. The active locale is held in the UI store
// (persisted per browser); `DocumentSettings` in App.tsx applies its writing
// direction to <html> and drives i18next. Persian is right-to-left.
export const SUPPORTED_LOCALES = ["fa", "en"] as const;
export type Locale = (typeof SUPPORTED_LOCALES)[number];

export const DIRECTION: Record<Locale, "rtl" | "ltr"> = { fa: "rtl", en: "ltr" };

void i18n.use(initReactI18next).init({
  resources: { en: { translation: en }, fa: { translation: fa } },
  lng: "fa",
  fallbackLng: "en",
  interpolation: { escapeValue: false },
});

export default i18n;
