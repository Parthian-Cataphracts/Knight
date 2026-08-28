# Phase 25 — how it was verified, and what verifying it found

Phase 25 had one exit criterion: **a Feature installed on a store whose code is
not in this repository, verified through a browser rather than a test.**

BojanStore is that store — an ASP.NET Core shop with a Next.js panel, in its own
repository, built by somebody else's roadmap. Phase 21 said it was "wired" on the
strength of the agent library compiling against it. Nothing had ever been
delivered to it, and this phase found out why: three separate things stood
between the library existing and a Feature actually working, and each of them
looked fine until two real processes had to agree.

---

## 1. What was built

| | |
|---|---|
| **A credential that is not a deploy** | `KnightConnection` + `IKnightCredentialStore`: an operator enters the credential, the agent reads it on its next pass, nothing restarts |
| **A connection screen** | «تنظیمات ← اتصال به نایت» in BojanStore's panel: connected or not, last handshake, last heartbeat, last job, last error, what has been delivered |
| **Serving what is delivered** | `UseKnightFeatureProxy` — middleware that forwards a Feature's declared prefixes to its service, signed, with the shopper's own credentials stripped |
| **The secret actually arriving** | The .NET `configure` step now writes `{version, values, secrets}` like every other reference store |
| **The store naming itself** | `X-Knight-Store` on every forwarded request, from the id the handshake taught it |
| **BojanStore's own half** | The agent vendored under `backend/vendor`, its event catalogue and UI slots, its `scope`-claim identity mapping, and an owner-only settings surface |

The store-side half lives in the BojanStore repository, in one commit. The
library half is here, in `stores/dotnet-store-agent`, and is the same library any
other ASP.NET Core store uses.

### Why the credential moved out of configuration

Connecting a shop meant an environment variable and a restart. The person who
owns a shop cannot restart a container, so in practice "connect us" would have
meant sending a client secret to whoever can — a credential that lets a control
plane install code, handed around in a chat window. It is now entered on a screen
by the one role that should hold it, and configuration remains the floor so a
store deployed with a credential needs nobody to press anything.

`Knight:SigningKeys` is the one thing that still has to be there first, and the
connection refuses rather than pretending: a store that trusts no key would fail
every delivery at `verify` after being told it was connected.

---

## 2. What verifying it found

### The agent recorded a Feature and served none of it

`configure` and `enable` wrote a registry entry, and nothing anywhere forwarded a
request. A .NET store could install `subscriptions`, report success to KNIGHT,
show it in a list, and answer 404 on every route the Feature declared. Nobody
would have found that from this side: the store says installed, KNIGHT says
installed, and only a shopper clicking the thing discovers otherwise.

### The shared secret was delivered and thrown away

The .NET `configure` step wrote `configuration.ValuesJson` alone. The secret
KNIGHT issues per store and rotates — the whole of phase 24 — arrived in the job
payload and was dropped on the floor. The failure it produces is a store that
takes delivery successfully and is then refused by the service, with nothing on
either side saying why.

### A forwarded request that is signed and anonymous

The first live request came back `401 signature.missing` with a perfectly correct
signature on it. The service looks a caller up by `X-Knight-Store` before any
cryptography happens, and the middleware never sent one. The store id cannot be
configured — a store learns it at the handshake — so the fix carries a second
rule with it: a store that has never connected is refused *here*, with a 503 that
says so, rather than at the service with a 401 that blames the signature.

### Requiring a signing key at start-up broke a store that was not connected yet

Validating `Knight:SigningKeys` unconditionally took 618 of BojanStore's own
tests down over a key no test needs. A store is added to this library long before
it has a credential. The rule was right and the place was wrong.

### The panel found two of its own

BojanStore's suites caught what nothing in this repository could: a screen key
the API's own catalogue did not know (a grant that ticks, saves and comes back
empty), and two icon names the shipped font subset does not carry. Both are guard
tests that repository already had, and both fired on the first run.

---

## 3. How to test it

Five processes. It is worth saying that out loud: this is the first phase where
the thing being verified spans two repositories.

```bash
# 1. KNIGHT
ServiceControlPlane__Secrets__subscriptions=<control secret> \
  dotnet run --project backend/src/Knight.Api

# 2. The Feature's service
cd services/subscriptions
SUBSCRIPTIONS_DEBUG=true SUBSCRIPTIONS_CONTROL_SECRET=<control secret> \
  python manage.py runserver 127.0.0.1:8140

# 3. BojanStore's API, with the agent configured but no credential
cd ../../../BojanStore/backend
Knight__FeatureRoot=<a writable directory> \
Knight__SigningKeys__dev=<the public key KNIGHT signs with> \
  dotnet run --project src/Bojan.Api

# 4. Its panel
cd ../frontend && pnpm dev:admin
```

In KNIGHT, onboard the shop the way an operator would: customer, activate,
store, activate, credential, a subscription on a plan, and the `subscriptions`
entitlement. The manifest's `base_url` is what KNIGHT and the store both call, so
a local run needs a version published with a local URL.

Then, **in the browser**, at `http://localhost:3001/settings/knight`:

1. The screen says **وصل نشده**. Paste the base URL, client id and client secret
   and press اتصال.
2. It changes to **در انتظار دست‌دادن** — the credential is recorded and the
   handshake has not happened yet, which is the honest thing to show.
3. Within a poll interval it is **متصل**, with the store's name as KNIGHT knows
   it and the times of the last handshake and heartbeat. **No restart.**
4. Install `subscriptions` from KNIGHT and issue the store its shared secret
   (`POST /api/v1/installations/service-secret`). The screen lists
   `subscriptions 2.1.0 · فعال · سرویس بیرونی`, the three routes this shop now
   serves, and where its screens hang.

Then check the shop is actually serving it:

```bash
curl http://localhost:7001/api/features/subscribe/
# {"service": "subscriptions", "store": "bojan-store", …}

curl -o /dev/null -w '%{http_code}\n' http://localhost:7001/api/features/admin/subscriptions/   # 403
curl -o /dev/null -w '%{http_code}\n' -X DELETE http://localhost:7001/api/features/subscribe/    # 405
curl -o /dev/null -w '%{http_code}\n' http://localhost:7001/api/features/nothing/                # 404
```

The first is the gate: a Feature's own service answering through a store whose
code is not in this repository. The other three are the store refusing on the
service's behalf — a staff route to a signed-out caller, a method the manifest
did not declare, and a prefix no Feature claims falling through to the shop's own
404.

---

## 4. The numbers

| | |
|---|---|
| .NET store agent | **51** tests, up from 31 |
| KNIGHT backend | 691 unit, 13 architecture, 164 integration |
| BojanStore | 193 domain, 418 API, 377 frontend |
| Browser | the panel screen, driven by hand: connect, wait, watch it arrive |

Screenshots are not in this report because the browser pane in the session that
ran it could not composite frames; what is recorded instead is the page's own
text, read back after each step.

---

## 5. What is still not done

- **Phonix.** No write access to that repository from here, and the patch on the
  desktop is the product owner's to apply. Carried forward.
- **A `dotnet` Feature.** BojanStore took delivery of a Feature that is a
  *service*, which needs no runtime at all — so the in-process .NET path is still
  the one runtime whose delivery has never been exercised against a real store.
- **UI mounts are listed, not mounted.** The panel shows where a Feature's
  screens want to hang; nothing renders them yet. That is the next honest piece
  of work on this seam and it belongs with phase 26's operational screens.
- **Nothing issues the credential on install.** Carried from phase 24: the
  service secret is still asked for by hand, and this phase is the second time
  that omission cost a manual step.
- **The connection status is per-process.** A restart forgets the last job it
  ran, because the status describes this process rather than the shop. It is the
  right default and it does mean a freshly restarted store shows «—» beside a job
  it really did run.
