// Persian, merchant-readable provisioning status — keyed off the coarse `state`
// the API returns, so the portal never shows the API's English friendlyStatus.
// (The backend keeps sending English for the operator side; we localise for the
// merchant here without a backend rebuild.)

const STATE_TEXT: Record<string, string> = {
  none: "هنوز راه‌اندازی شروع نشده است.",
  provisioning: "در حال آماده‌سازی فروشگاه شما…",
  awaiting_operator: "فروشگاه شما در صف راه‌اندازی است؛ به‌زودی فعال می‌شود.",
  ready: "فروشگاه شما آماده است.",
  failed: "در بالا آوردن فروشگاه مشکلی پیش آمد؛ تیم ما مطلع شد.",
};

/** Persian status line for a provisioning state, falling back to the API text. */
export function provisioningStatusText(state: string, fallback: string): string {
  return STATE_TEXT[state] ?? fallback;
}

/**
 * Whether a store's domain is a real, publicly reachable one. Self-service stores
 * on this deployment get an internal `…​.stores.knight.local` placeholder that no
 * browser can open, so the portal must not present its admin/storefront links as
 * if they were live — it shows a preview note instead.
 */
export function isPublicDomain(domain: string | undefined | null): boolean {
  if (!domain) return false;
  const d = domain.toLowerCase();
  return !d.endsWith(".local") && !d.endsWith(".localhost") && d.includes(".");
}
