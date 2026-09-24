# Task board

Claim a task by filling owner + lease-until before editing its scope. A stale lease
(past its time) may be reclaimed — note it in `activity.md`. Statuses:
PLANNED → IN_PROGRESS → VERIFICATION_PENDING → VERIFIED (or FAILED).

| id | task | scope (files/modules) | owner | status | lease-until |
|----|------|-----------------------|-------|--------|-------------|
| T1 | Real store hosting for self-service signups (wildcard DNS/TLS + per-store deploy) | Provisioning, infra | — | PLANNED (owner-gated) | — |
| T2 | Wire a real payment gateway (replace SimulatedPaymentProvider) | PlatformBilling, Billing | — | PLANNED (owner-gated) | — |
| T3 | Configure real SMTP (replace log-fallback verification) | Onboarding, appsettings | — | PLANNED (needs creds) | — |
| T4 | Automatic Admin go-live: AI key + channel tokens | AutoAdmin, portal | — | PLANNED (needs creds) | — |
| T5 | Security review + backup custody + release call (Phase 29) | — | — | PLANNED (owner-gated) | — |

_No tasks currently claimed._
