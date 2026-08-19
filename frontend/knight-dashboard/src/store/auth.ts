import { create } from "zustand";
import { apiRequest, setAccessToken } from "@/lib/api/client";
import { connectRealtime, disconnectRealtime } from "@/lib/realtime/connection";
import type { CurrentUser, LoginResponse } from "@/lib/api/types";

interface AuthState {
  user: CurrentUser | null;
  status: "unknown" | "authenticated" | "anonymous";
  signIn: (user: CurrentUser, accessToken: string) => void;
  signOut: () => void;
  /** Exchanges the HttpOnly refresh cookie for a session, so a reload does not sign the user out. */
  restore: () => Promise<void>;
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

    // Opened after the token is set, since the hub authenticates with it. A
    // failure to connect is swallowed inside: nothing on the dashboard depends
    // on realtime being available.
    connectRealtime();
  },
  signOut: () => {
    // Closed before the token is cleared, so the connection ends deliberately
    // rather than being dropped by the server on the next unauthenticated
    // reconnect attempt.
    disconnectRealtime();
    setAccessToken(null);
    set({ user: null, status: "anonymous" });
  },
  restore: async () => {
    try {
      // The refresh token lives in an HttpOnly cookie the browser attaches
      // itself; there is nothing for the client to remember, and nothing in
      // localStorage to steal (docs/authentication.md section 1).
      const session = await apiRequest<LoginResponse>("/auth/refresh", { method: "POST", body: {} });

      if (session.status === "succeeded" && session.accessToken && session.user) {
        setAccessToken(session.accessToken);
        set({ user: session.user, status: "authenticated" });
        connectRealtime();
        return;
      }

      set({ user: null, status: "anonymous" });
    } catch {
      // No usable cookie: an ordinary first visit, not an error worth showing.
      set({ user: null, status: "anonymous" });
    }
  },
  can: (permission) => get().user?.permissions.includes(permission) ?? false,
}));
