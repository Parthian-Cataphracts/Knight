import type { ReactNode } from "react";
import { useAuthStore } from "@/store/auth";
import { LoginPage } from "@/features/auth/pages/LoginPage";

/**
 * Gate for the authenticated shell. This is convenience, not security: the API
 * authorizes every request independently (docs/authorization.md).
 */
export function RequireAuth({ children }: { children: ReactNode }) {
  const status = useAuthStore((state) => state.status);
  if (status !== "authenticated") return <LoginPage />;
  return <>{children}</>;
}
