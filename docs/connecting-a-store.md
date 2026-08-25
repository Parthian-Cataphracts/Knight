# Connecting a store to KNIGHT

Status: **authoritative**. The contract itself is
[`contracts/store-integration.schema.json`](contracts/store-integration.schema.json)
and [`contracts/store-integration.samples.json`](contracts/store-integration.samples.json);
this is how to implement it.

## 1. A store is not a framework

KNIGHT manages independent customer applications. The reference implementation
is Django ([`store-integration.md`](store-integration.md)), and that is a worked
example rather than a requirement: what makes an application a store is the
handful of HTTP calls below. A .NET service, a Node service, a PHP shop and a
Django one are all the same thing to KNIGHT, and it never learns which it is
talking to beyond the `runtime` string the store volunteers.

Two rules hold whatever the stack is, and they are the reason for most of what
follows:

- **KNIGHT never connects to a store's database and never depends on its
  schema.** Everything it knows, the store told it.
- **Entitlement and installation are separate facts.** KNIGHT decides what a
  customer is owed. Whether the code that provides it is present, and healthy,
  is a different question with a different answer.

## 2. What you do in the dashboard

All of it, before the store is ever run:

1. **Create the customer.** The company that owns the store.
2. **Register the store** - name, slug, primary domain, environment. It starts
   at `NotRegistered`.
3. **Issue a credential.** The secret is displayed **exactly once** and stored
   only as a hash. If it is lost, rotate the credential rather than looking for
   it.
4. **Start domain verification.** KNIGHT issues a token to publish.

The store's own page names the one reason it cannot connect, in a sentence - a
suspended customer, an inactive store, an unproven domain - because the store's
logs deliberately will not tell you. A store that is refused cannot be allowed
to learn whether the secret was wrong or the customer was suspended.

## 3. What your service calls

Base path `/api/v1/ingest` on the KNIGHT host.

### The handshake

```http
POST /api/v1/ingest/handshake
Content-Type: application/json

{
  "clientId": "...",
  "clientSecret": "...",
  "environment": "Production",
  "storeVersion": "1.4.2",
  "runtime": "dotnet/10.0",
  "nonce": "6f1a2b3c4d5e6f708192a3b4"
}
```

The response carries a short-lived bearer token and the key everything else is
verified with:

```json
{
  "storeId": "...", "storeName": "...", "slug": "...",
  "environment": "Production",
  "integrationStatus": "Pending",
  "accessToken": "...", "tokenType": "Bearer",
  "expiresIn": 1800, "expiresAt": "...",
  "entitlementSigningKey": "<base64 HMAC key>",
  "domainVerificationOutstanding": true,
  "domainVerificationPath": "/.well-known/knight-domain-verification",
  "heartbeatSeconds": 60,
  "featureRefreshSeconds": 300
}
```

Three things here are worth implementing deliberately:

- **The `nonce` is used once.** A second handshake presenting it inside the
  window is refused. Generate a fresh one each time; never derive it from
  anything stable.
- **The token is short by design.** It is the one credential in this system that
  cannot be rotated, so it is made not worth stealing instead. Re-handshake on a
  401 rather than caching it past its expiry.
- **`heartbeatSeconds` and `featureRefreshSeconds` are KNIGHT's decision**, not
  yours. Honour what the response says.

### Heartbeat

```http
POST /api/v1/ingest/heartbeat
Authorization: Bearer <accessToken>

{ "environment": "Production", "status": "healthy", "storeVersion": "1.4.2",
  "features": ["storefront", "analytics"] }
```

`status` is `healthy`, `degraded` or `unhealthy`. `features` are the slugs the
store has actually **installed** - sending them is what lets KNIGHT compare
entitlement against installation and notice when they disagree.

### Entitlements

```http
GET /api/v1/ingest/features
Authorization: Bearer <accessToken>
```

The signed set of what this customer is owed. Section 5 is how to verify it.

### Errors, events and logs

`POST /api/v1/ingest/errors`, `/events`, `/logs`. Optional, batched, capped per
batch and rate-limited per store. A store that ships none of them still works;
it just cannot be diagnosed from the dashboard.

## 4. What your service serves

This is the half somebody has to write, and where a new integration usually
fails. Until both of these work, the store stays `Pending`.

### `GET /api/knight/health`

Answer with:

```json
{
  "status": "healthy",
  "checkedAt": "2026-08-19T12:00:00Z",
  "version": "1.4.2",
  "environment": "Production",
  "dependencies": { "database": { "status": "healthy", "latencyMs": 3 } },
  "features": ["storefront", "analytics"]
}
```

**The request is signed, and an unsigned one must be refused with 401.** This
payload lists the store's version, its dependencies and its installed features,
which is exactly the reconnaissance an attacker wants - so the endpoint is
authenticated rather than public.

KNIGHT sends:

| Header | |
|---|---|
| `X-Knight-Store` | the store id |
| `X-Knight-Timestamp` | Unix seconds |
| `X-Knight-Nonce` | 24 hex characters |
| `X-Knight-Signature-Version` | `1` |
| `X-Knight-Signature` | base64 HMAC-SHA256 over the canonical form |

Verify the signature, **and** reject a timestamp outside your clock-skew window.
Without the second check, a captured request can be replayed indefinitely.

### `GET /.well-known/knight-domain-verification`

Serve the token KNIGHT issued, as plain text, unauthenticated. It is compared
**exact after trimming** and read up to 4KB: a page that merely *contains* the
token is not enough, and a store is not `Connected` until KNIGHT has read it
there. A credential says who you are; it says nothing about who answers on that
domain.

## 5. The two signed strings

Both are flat, pipe-separated text carrying Unix seconds - not JSON. Two
languages will never agree byte-for-byte on JSON, over date formatting, property
order, or how an absent value is rendered, and a signature that only sometimes
verifies is worse than none at all.

Both use **base64 HMAC-SHA256** under `entitlementSigningKey` from the
handshake. One key, two uses; there is no second secret to store.

**Entitlements** - KNIGHT signs, the store verifies:

```
knight-entitlements|1|{storeId}|{customerId}|{environment}|{issuedAt}|{staleAfter}|{slug}:{expiresAt or -},...
```

Features sorted by slug, ordinal - never by a database collation or the culture
the process runs in. An absent expiry is a single hyphen, so it can never be
confused with a field that was left out.

**Requests** - KNIGHT signs, the store verifies:

```
knight-request|1|{METHOD}|{path}|{timestamp}|{nonce}
```

The path only, never the host: a proxy in front of the store may legitimately
rewrite the host, and binding the signature to it would break every store behind
one.

Worked examples with the exact expected output are in
[`contracts/store-integration.samples.json`](contracts/store-integration.samples.json).

## 6. Enforcing what the customer paid for

The rules are the same in any language, and each one exists because the obvious
alternative fails badly:

- **Refresh on the schedule KNIGHT gave you, and cache the result.**
- **Verify the signature every time, including on the cached copy**, and discard
  a set that does not verify rather than enforcing it. That is what makes the
  cache safe to keep somewhere other processes can reach.
- **When KNIGHT is unreachable, keep enforcing the last known good set for a
  bounded grace period.** After it, fall back to a *minimum safe set* - the
  capabilities every store has, and nothing anybody pays for.

The fallback must never be "allow everything". A control plane being unreachable
is a bad afternoon; every store on earth unlocking its paid features because of
it is a different kind of problem.

And keep the two questions apart. `isEnabled(slug)` asks whether it is paid for.
Whether the code is present is a separate question with a separate answer, and
collapsing them into one boolean is the mistake
[`adr/0019`](adr/0019-entitlement-as-an-explicit-record.md) exists to prevent.

## 7. Proving it

```bash
python stores/conformance/knight_conformance.py check \
  --knight-url https://knight.example.com \
  --client-id ... --client-secret ... \
  --store-url https://cafe1.ir
```

Every requirement above, checked against your running service - including the
ones that are about refusing things: an unsigned health request, a signature
made over a different path, an hour-old request, a replayed handshake nonce.
See [`../stores/conformance/README.md`](../stores/conformance/README.md).

## 8. What this does not give you

Everything above makes a store of any stack **managed, entitled and observed**.
It does not give it **automated Feature delivery** - KNIGHT building, signing
and installing versioned code into it.

That pipeline is not conceptually tied to Django. The job vocabulary is a closed
list of names - `Install`, `Upgrade`, `ApplyConfiguration`, `Enable`, `Disable`,
`Uninstall`, `Rollback`, carried out as `preflight`, `fetch`, `verify`, `backup`,
`install`, `migrate`, `configure`, `enable`, `disable`, `reload`, `healthcheck` -
and a store may carry each of them out however its own runtime does.

The manifest is tied to Django, though.
[`ManifestReader`](../backend/modules/FeatureRegistry/Domain/ManifestReader.cs)
refuses a version whose manifest has no `django:` block, and validates
`app_label` and `installed_app` as Python identifiers. So today a Feature cannot
be *published* for a non-Django store, never mind installed into one. Making the
manifest runtime-neutral is an open decision rather than an oversight, and it is
recorded in [`risks.md`](risks.md).

Until that decision is taken, a non-Django store is entitled by KNIGHT and
enforces those entitlements itself, and its code is deployed the way that team
already deploys code.
