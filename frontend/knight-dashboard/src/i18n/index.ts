import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import fa from "./fa.json";
import en from "./en.json";

export const SUPPORTED_LOCALES = ["fa", "en"] as const;
export type Locale = (typeof SUPPORTED_LOCALES)[number];

export const DIRECTION: Record<Locale, "rtl" | "ltr"> = { fa: "rtl", en: "ltr" };

void i18n.use(initReactI18next).init({
  resources: { fa: { translation: fa }, en: { translation: en } },
  lng: (import.meta.env.VITE_DEFAULT_LOCALE as Locale | undefined) ?? "fa",
  fallbackLng: "fa",
  interpolation: { escapeValue: false },
});

export default i18n;
