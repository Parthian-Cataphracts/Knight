import { create } from "zustand";

type Theme = "dark" | "light";
type Locale = "fa" | "en";

interface UiState {
  theme: Theme;
  locale: Locale;
  sidebarCollapsed: boolean;
  mobileNavOpen: boolean;
  setTheme: (theme: Theme) => void;
  toggleTheme: () => void;
  setLocale: (locale: Locale) => void;
  toggleSidebar: () => void;
  setMobileNavOpen: (open: boolean) => void;
}

const STORAGE_KEY = "knight.ui";

function readStored(): Partial<Pick<UiState, "theme" | "locale" | "sidebarCollapsed">> {
  try {
    return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "{}") as Partial<UiState>;
  } catch {
    return {};
  }
}

function persist(state: Pick<UiState, "theme" | "locale" | "sidebarCollapsed">): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
}

const stored = readStored();

export const useUiStore = create<UiState>((set, get) => ({
  theme: stored.theme ?? "dark",
  locale: stored.locale ?? ((import.meta.env.VITE_DEFAULT_LOCALE as Locale | undefined) ?? "fa"),
  sidebarCollapsed: stored.sidebarCollapsed ?? false,
  mobileNavOpen: false,
  setTheme: (theme) => {
    set({ theme });
    const { locale, sidebarCollapsed } = get();
    persist({ theme, locale, sidebarCollapsed });
  },
  toggleTheme: () => get().setTheme(get().theme === "dark" ? "light" : "dark"),
  setLocale: (locale) => {
    set({ locale });
    const { theme, sidebarCollapsed } = get();
    persist({ theme, locale, sidebarCollapsed });
  },
  toggleSidebar: () => {
    const sidebarCollapsed = !get().sidebarCollapsed;
    set({ sidebarCollapsed });
    const { theme, locale } = get();
    persist({ theme, locale, sidebarCollapsed });
  },
  setMobileNavOpen: (mobileNavOpen) => set({ mobileNavOpen }),
}));
