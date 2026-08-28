# Phase 24 — how it was verified, and what verifying it found

Phase 24 had one exit criterion: **each store has its own shared secret with each
service, issued by KNIGHT and rotatable without an outage, and a store whose
entitlement was revoked cannot call the service at all.**

It was chosen because phase 23 left a credential nobody could change. The secret
was one environment variable an operator typed into the store and one column an
operator typed into the service — correct for one store, and an incident at ten:
nobody could rotate it without refusing every request in flight, nobody could say
when it had been issued or who held it, and withdrawing an entitlement stopped
the store forwarding without stopping the service answering.

The drill named it honestly at the time: its constant is still called
`drill-shared-secret-not-for-a-deployment`.

---

## 1. What was built

| | |
|---|---|
| **Secrets with lifetimes** | `StoreSecret` on the service: a start, an optional expiry, a revocation date. A request verifies against **any** currently usable secret |
| **Rotation that overlaps** | Issuing a new one sets the old ones expiring rather than replacing them. An expiry only ever moves downwards |
| **A control plane on the service** | Four routes under `/knight/`, signed with a secret that is not any store's: register, rotate, revoke, describe |
| **KNIGHT issuing it** | `IServiceCredentialService` mints the secret, tells the service, and delivers it to the store as a configuration secret down the existing path |
| **Revocation that reaches the service** | Withdrawing an entitlement disables the installation **and** ends the store's secrets |
| **The store preferring what was delivered** | `secret_for` reads the delivered configuration first and the environment second, per request |
| **Housekeeping** | `knight_maintain` forgets used nonces and throws away the value of long-dead secrets. A compose sidecar runs it hourly |
| **OAuth renewal** | `external-marketplaces` 1.1.0 renews the refresh tokens it had stored since it was written and never used |
| **Drill step 14** | The gate, walked against real processes |

The decision is recorded in
[`adr/0034`](adr/0034-a-shared-secret-has-a-lifetime.md).

### The order, which is the design

The service is told first and the store second, everywhere.

A store holding a credential the service has not heard of is a store signing with
something that cannot verify, and that window is an outage. So if the service
refuses or does not answer, **nothing is delivered** and the store carries on
with the secret it already has. The reverse order would have been easier to write
and would have failed exactly when a service was having a bad day.

The same reasoning gives revocation its order: the installation is disabled
first, so a service that cannot be reached cannot stop the store-side half from
happening, and then the credential is revoked.

### Why the overlap is an hour

A store stops using its old secret only once its agent has taken delivery of the
new configuration, and taking delivery is a queued job the agent polls for. A
window shorter than the polling interval would cut off a store that is doing
exactly what it was told to do. An hour is the default, a day is the ceiling, and
zero is allowed because that is what a leak needs.

---

## 2. What verifying it found

### The install loop hid the reason it was failing

The drill's install loop reports "did not settle; something is queueing work that
never completes" — a description of the symptom. Running it locally against a
control plane whose artifacts had been signed by an earlier key, the store had
been printing `signature.invalid` on every pass and the drill never showed it.

`run_jobs` now prints what the store said whenever it succeeded at nothing. The
failure itself was environmental, and the twenty minutes spent finding it were
not.

### A store with no usable secret must fail like a bad signature

The first version of the verification asked the store's secrets for a match and
said nothing about a store that had none. That answers a caller "this store
exists and has no credential", which is a fact about somebody else's shop given
to somebody who has not proved they are it. It is now the same refusal as a
signature that did not verify.

### PostgreSQL considers two NULLs distinct

The nonce table is unique on `(store, nonce)`, and KNIGHT's own requests belong
to no store. The obvious change — make the column nullable — would have left the
control plane's requests replayable without limit, which is the one caller that
can issue a credential. It needs its own partial unique constraint, and now has
one.

### A forgotten secret cannot be an empty string

Blanking spent secrets collides with the per-store uniqueness constraint the
moment a second one is forgotten, turning a housekeeping sweep into a crash. The
value is replaced with a marker naming its own row.

---

## 3. How to test it

### The whole thing, in one command

```bash
python tools/delivery-drill/drill.py
```

Step 14 is this phase. It issues a credential over the service's control plane,
watches the store take delivery of it, checks that **both** secrets verify during
the overlap, that the store's proxy answers without a restart, and that
withdrawing the entitlement is refused by the service itself.

The API must be started with the control secret the drill uses:

```bash
ServiceControlPlane__Secrets__subscriptions=drill-control-secret-not-for-a-deployment
```

### By hand, if you want to watch a rotation

Start the service with a control secret of its own:

```bash
cd services/subscriptions
SUBSCRIPTIONS_DEBUG=true SUBSCRIPTIONS_CONTROL_SECRET=a-control-secret python manage.py runserver 8100
```

Point KNIGHT at it — the manifest's `base_url` is what KNIGHT calls, so a local
run needs a manifest published with a local URL — and set the same value in the
API's `ServiceControlPlane__Secrets__subscriptions`. Then, with a store that has
`subscriptions` installed:

```bash
curl -X POST http://localhost:5008/api/v1/installations/service-secret \
  -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" \
  -d '{"storeId":"<store>","featureId":"<feature>","overlapSeconds":600}'
```

Expect `{"secretName":"SUBSCRIPTIONS_SERVICE_SECRET","rotated":true,...}` and no
secret in the response. On the service:

```bash
python manage.py knight_store list
```

Expect **2 secret(s)** while the window is open, and 1 after it closes. The store
takes delivery when its agent next runs:

```bash
cd stores/reference-store
python manage.py knight_apply_job --max-jobs 5
cat "$KNIGHT_FEATURE_ROOT/subscriptions.config.json"
```

The new value is in `secrets`. The store needs no restart: the file is read per
request, on purpose.

### The gate

**Rotate with a request in flight and lose nothing.** Sign a request with the
*old* secret after the rotation — it is still answered, because the service
accepts every secret whose window is open:

```bash
python manage.py knight_store list   # 2 secret(s)
```

**Revoke an entitlement and watch the next call be refused by the service.**

```bash
curl -X POST http://localhost:5008/api/v1/customers/<customer>/entitlements/<feature>/revoke \
  -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" -d '{"reason":"testing"}'
python manage.py knight_store list   # 0 secret(s), DISABLED
```

Any request from that store now gets a 401 from the service, whatever the store's
own registry still says.

### Housekeeping

```bash
python manage.py knight_maintain
```

Expect `forgot N nonce(s), blanked M spent secret(s).` Safe at any interval and
safe to run twice.

---

## 4. The numbers

| | |
|---|---|
| Backend | **691 unit**, **13 architecture**, **164 PostgreSQL-backed integration** |
| Reference store | **841**, nothing skipped |
| Subscriptions service | **57** |
| Delivery drill | **15 steps**, every assertion green |

---

## 5. What is still not done

- **The credential is issued by hand.** KNIGHT has the endpoint and nothing calls
  it on an install: an external Feature's first install still needs somebody to
  ask for a secret. Wiring it into the install path is small and belongs with
  phase 25, where a second real store makes the omission obvious.
- **Nothing rotates on a schedule.** Rotation is possible, which is the property
  that was missing; a policy that rotates every ninety days is phase 26's
  operational work, with the rest of the alerting and runbooks.
- **The control secret is a deployment secret with no rotation story of its own.**
  It is one value per Feature, held by KNIGHT, and changing it means changing it
  in two places at once. That is the same problem this phase solved one level
  down, and it is smaller — there is one holder rather than a fleet.
- **The service is still not deployed anywhere.** It runs in `docker compose` and
  in CI. Phase 27.
