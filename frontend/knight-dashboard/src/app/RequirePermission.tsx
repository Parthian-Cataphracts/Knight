import type { ReactNode } from "react";
import { useAuthStore } from "@/store/auth";
import { NotAuthorized } from "@/components/ui/NotAuthorized";

/**
 * Renders its children only when the operator holds the permission, and the
 * not-authorized block otherwise. The URL is left as it is rather than
 * redirected, so a bookmarked or shared link lands on an explanation instead of
 * silently bouncing to the dashboard.
 *
 * The guard sits outside the lazy screen it wraps, so a screen an operator may
 * not see is never even fetched — the chunk stays unloaded.
 */
export function RequirePermission({
  permission,
  children,
}: {
  permission: string;
  children: ReactNode;
}) {
  const can = useAuthStore((state) => state.can);
  return can(permission) ? <>{children}</> : <NotAuthorized />;
}
