# Durable decisions (short: what + why)

- **Modular monolith control plane, not microservices** — the checkout→entitlement
  →provision→install workflow is naturally transactional; keep it in one DB. Stores
  and heavy Features are the parts that are genuinely separate processes. (ADR 0010+)
- **A Feature is a versioned deployable package, not a boolean flag** — built once,
  delivered to entitled stores. Base features ship in the store image and are never
  delivered (ADR 0024) — this is why provisioning skips them.
- **Entitlement ≠ installation** — separate facts, tracked separately.
- **Self-service SaaS pivot** (ADR 0035) — payment auto-drives provisioning.
- **Persian-first, priced in Toman (IRT)** — merchant-facing surfaces localized at
  the display layer; seed reconciles prices in place.
- **No AI traces in the repo** — commits authored as a human maintainer (CLAUDE.md).
