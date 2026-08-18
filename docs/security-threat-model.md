# Security Threat Model

Status: **authoritative proposal**.

## 1. Assets

| Asset | Why it matters |
|---|---|
| Store credentials and agent tokens | Compromise lets an attacker impersonate a store or server |
| Customer business data visible in KNIGHT (errors, logs, metrics) | Leaks a customer's operational internals to a competitor |
| Subscription and billing data | Financial fraud, unauthorised feature use |
| Administrative accounts | Full control over all customers |
| Audit trail | Must remain trustworthy after an incident |
| Store availability | KNIGHT must never be able to take a store down |
| Feature packages and signing keys | **KNIGHT delivers executable code**; a forged package runs inside every entitled store |
| The agent's execution capability | Its privileges on the store's server make it the highest-value target in the system |

## 2. Trust boundaries

```
Browser ──1── KNIGHT API ──2── KNIGHT DB / Redis
                   │
                   ├──3── Store Management API (customer-operated network)
                   └──4── Agent (server, possibly customer-managed)
```

Boundaries 3 and 4 face partially untrusted networks and partially untrusted
operators (`CustomerManaged` hosting). Data arriving from them is input, never
instruction.

## 3. Threats and controls (STRIDE-flavoured)

| # | Threat | Control |
|---|---|---|
| T1 | Credential stuffing / brute force on login | Rate limiting on `auth`, lockout, MFA for platform roles, breach-aware password policy |
| T2 | Refresh-token theft | Rotation with reuse detection, HttpOnly cookie, session binding, revoke-family on anomaly |
| T3 | Cross-customer data access (IDOR) | Persistence-level customer filter + `404` on foreign resources + mandatory isolation tests |
| T4 | Privilege escalation via role editing | `role.manage` restricted to SuperAdmin; system roles immutable; every change audited |
| T5 | Store impersonation | Per-store credentials, hashed at rest, environment binding, optional IP allow-list and request signing |
| T6 | Replay of ingestion payloads | Timestamp window + nonce in Redis + `Idempotency-Key` |
| T7 | Ingestion flood / storage exhaustion (DoS by a store) | Per-store rate limits, batch size caps, bounded retention, backpressure with `429`, drop-with-counter |
| T8 | Malicious payload injection through error/log data | Strict schema validation, size caps, no HTML rendering of raw payloads (escape everywhere), stored as data only |
| T9 | Prompt/log poisoning of future AI analysis | Treat all ingested text as untrusted data; never execute or follow instructions found in it |
| T10 | SSRF via store-registered domains | Outbound calls only to registered, verified hosts; deny private/link-local ranges; no redirect following |
| T11 | Agent turned into remote shell | Agent has no inbound port and no command channel; it pulls **typed** jobs only and implements a closed step vocabulary (see T21/T22) |
| T12 | Secret leakage in logs, errors, or API responses | Central redaction, ProblemDetails without internals, secret scanning in CI, secrets shown once |
| T13 | Tampering with the audit trail | Append-only writes, no update/delete endpoints, restricted DB grants |
| T14 | Frontend-only authorization bypass | Every endpoint policy-checked server-side; UI permissions are cosmetic |
| T15 | Entitlement bypass in a store | Store enforces server-side; entitlement payload signed; short TTL |
| T16 | Environment cross-talk (prod store → dev KNIGHT) | `env` claim in every token, environment match required at handshake |
| T17 | Supply-chain compromise | Pinned dependencies, lockfiles committed, dependency audit in CI |
| T18 | XSS/CSRF in the dashboard | React escaping, strict CSP, no `dangerouslySetInnerHTML`, tokens not in `localStorage`, SameSite cookies |
| T19 | **Malicious or forged Feature package** delivered to stores | Artifacts built only by our pipeline, signed offline-held key, detached signature + sha256 digest verified by the agent before install; unsigned artifacts refused |
| T20 | **Compromised package registry** serving a swapped artifact | Digest pinned in the immutable `FeatureVersion` record in KNIGHT, not taken from the registry; mismatch aborts and alerts |
| T21 | **Compromised KNIGHT issuing hostile jobs** | Fixed typed job vocabulary (no command strings); agent refuses unknown types; agent verifies signatures independently; all jobs audited; destructive job types require elevated permission |
| T22 | **Agent abused as a remote shell** | No shell, no arbitrary paths, no inbound port, least-privilege service account, only the declared lifecycle steps |
| T23 | Malicious/insecure code inside a legitimate Feature | Code review + tests before publish, manifest and package validation, dependency audit, only trusted authors may publish |
| T24 | Job token replay or theft | Short-lived, single-job, store-scoped tokens; one active job per store; idempotent steps |
| T25 | Feature configuration secrets leaking | Encrypted at rest in KNIGHT, delivered only in the job payload over TLS, never logged, never in job step output, never returned by a read API |
| T26 | Destructive migration triggered by a mistaken click | Preflight shows manifest reversibility and duration; irreversible operations require explicit confirmation; restore point recorded first |
| T27 | Cross-customer contamination through a shared registry | Registry holds no customer data; configuration is per-store and never bundled into an artifact |
| T28 | Signing key compromise | Keys held outside CI where possible, rotation procedure documented, ability to yank every version signed by a compromised key |

## 4. Domain verification

A store domain is claimed, not proven, at registration. Before a store is
`Connected`, ownership must be verified (DNS TXT or a well-known path) so a
customer cannot register someone else's domain and receive their telemetry.

## 4b. Feature delivery is a code-execution channel

Delivering a Feature means running our code inside a customer's application and
database. That makes the delivery path the most security-sensitive part of
KNIGHT. Non-negotiables:

1. Only artifacts built by our pipeline may be published.
2. Every artifact is signed; the agent verifies signature **and** the digest
   recorded in KNIGHT before installation.
3. Jobs carry typed operations only — never a command, script, or arbitrary URL.
4. The agent implements a closed set of steps and refuses everything else.
5. Every lifecycle operation is authorised, audited, and attributable to an
   actor (user or automation source).
6. Failure never leaves an undocumented state: rollback outcome is recorded,
   including `ManualInterventionRequired`.

## 5. Least privilege

- Database user for the API has no DDL rights in Production; migrations run
  under a separate user.
- Agents may report telemetry and execute lifecycle jobs for **their own
  store only**; they hold no read access to KNIGHT data.
- Publishing a Feature version is restricted to `feature.publish` holders and
  is always audited.
- Support roles are read-mostly; billing is separated from operations.

## 6. Incident readiness

- All credential revocation is a single dashboard action and takes effect
  immediately (no long-lived cached tokens beyond their short TTL).
- Security-relevant events (auth failures, revocations, isolation violations)
  raise alerts, not just log lines.
- A compromised store must be isolatable without touching other customers.

## 7. Open security questions

Tracked in `risks.md`: signing algorithm choice and key rotation procedure, IP
allow-listing feasibility for customer-managed hosting, PII classification of
ingested logs, and data-residency expectations.
