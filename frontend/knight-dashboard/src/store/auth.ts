import { create } from "zustand";
import type { CurrentUser } from "@/lib/api/types";
import { setAccessToken } from "@/lib/api/client";

interface AuthState {
  user: CurrentUser | null;
  status: "unknown" | "authenticated" | "anonymous";
  signIn: (user: CurrentUser, accessToken: string) => void;
  signOut: () => void;
  can: (permission: string) => boolean;
}

/**
 * Session state only. Permissions here drive what the UI offers - never what
 * it is allowed to do; the API enforces authorization (docs/authorization.md).
 */
export const useAuthStore = create<AuthState>((set, get) => ({
  user: null,
  status: "unknown",
  signIn: (user, accessToken) => {
    setAccessToken(accessToken);
    set({ user, status: "authenticated" });
  },
  signOut: () => {
    setAccessToken(null);
    set({ user: null, status: "anonymous" });
  },
  can: (permission) => get().user?.permissions.includes(permission) ?? false,
}));
