# 0033 — A Feature may be a service the store talks to, not only code it runs

**Status:** accepted
**Supersedes nothing. Extends [`0014`](0014-a-feature-is-a-deployable-package.md) and [`0032`](0032-a-feature-declares-its-runtime.md).**

## Context

`adr/0014` settled that a Feature is versioned, deployable functionality rather
than an entitlement flag, and it was right. `adr/0032` settled that a Feature
declares which runtime it is built for, and that the delivery path was never
Django's. Both remain true.

What neither anticipated is the cost of the second one at scale. Delivering code
into a store means a store agent per stack, and this repository now has three:
Python, JavaScript and .NET. Building the third is the evidence. It took a
library, fifteen step verbs, nineteen tests — and along the way the node agent
turned out to have been missing three verbs for three phases and three more for
four, so it could not roll back or uninstall anything and nobody had noticed.

That is the shape of the problem, and it multiplies the wrong way:

| | In-process | Per Feature | Per runtime |
|---|---|---|---|
| Build | a package, per runtime | ✓ | ✓ |
| Sign and version | one artifact per runtime per version | ✓ | ✓ |
| Install | unpack, migrate, mount, restart | ✓ | ✓ |
| Roll back | restore the tree **and** reverse the schema | ✓ | ✓ |
| Debug | in somebody else's process, against their database | ✓ | ✓ |

A roadmap of 150 Features across three runtimes is 450 packages to build, sign,
version, install, migrate and roll back — and every one of them runs inside a
store we do not operate, holding that store's database handle. The migration
half is the part that does not get better with effort: phase 19 found that a
rollback restored the package before reversing the schema, so the code went back
and the database did not, and the only reason that was ever found is that
somebody built two versions whose migrations genuinely differed.

There is a second cost that is easy to miss. **A Feature installed into a store
is deployed on that store's schedule.** Fixing a bug in `subscriptions` means
publishing a version and waiting for every store to take it, and until they do,
each of them is running a different one.

## Decision

**A Feature declares its architecture, and `external_service` means the store
runs none of its code.**

```yaml
architecture: external_service

service:
  base_url: https://subscriptions.knight.dev
  auth: hmac-sha256
  health: /healthz
  secret: SUBSCRIPTIONS_SERVICE_SECRET

webhooks:
  - event: order.placed
    path: /hooks/order-placed
    delivery: at-least-once

api_proxies:
  - prefix: subscriptions/
    upstream: /api/v1/subscriptions/
    methods: [GET, POST]
    identity: customer

ui_mounts:
  - slot: admin.sidebar
    label: Subscriptions
    path: /admin/subscriptions
    kind: iframe
```

KNIGHT delivers **a signed configuration document** rather than an archive:
which of the store's events to forward, which routes to proxy, where the screens
hang. The Feature runs once, wherever its author runs it. The store's database
never hears about it.

`architecture` sits **above** `runtime`. `runtime` answers "what language is this
code written for" and only means anything when there is code; `architecture`
answers "is there code at all". An external Feature has no runtime, and the
reader enforces the pairing in both directions — an in-process Feature always has
a runtime block, an external one never does — so nothing downstream has a third
case to consider.

### It is still signed

Skipping the signature because "it is only configuration" would be the worst
decision available here. The document tells a store to forward its customers'
requests to a URL. A store that acted on an unverified one would wire a proxy
route to whatever host answered the download URL, which is a strictly better
attack than tampering with code, because it needs no code at all.

So the configuration **is** the artifact: same digest, same detached ECDSA
signature over the same ASCII digest string, same `fetch` then `verify` before
anything is acted on. The only thing that changed is that the bytes are JSON
instead of a zip, and the artifact store keeps it under a `.json` name so the
directory does not lie to whoever looks in it.

### No new step verbs

The external pipelines are built entirely from verbs the three agents already
implement:

| Job | Steps |
|---|---|
| Install / upgrade | `preflight` `fetch` `verify` `backup` `configure` `install` `enable` `healthcheck` |
| Rollback | `restore-package` `configure` `enable` `healthcheck` |
| Uninstall | `disable` `backup` `remove-package` |

`install` means "make this Feature present in this store", and for a service that
is registering its webhooks and wiring its proxy routes — the same relationship
every runtime already has to that verb, where Django unpacks a Python
distribution and node unpacks an npm package (`0032` §3).

This is not tidiness. **A store that meets a verb it does not know refuses the
whole job**, and the two phases before this one each found an agent missing verbs
nobody had noticed. Adding `register-webhooks` would have broken every store on
the day it shipped and only for the Features that most needed to work. There is a
test asserting that no external pipeline names a verb the in-process one does
not.

### What each side validates

KNIGHT validates the **shape** of an event name at publish, because it cannot
know what any particular store emits. The store validates the **name** at
install, because it is the only thing that can. Without the second, a Feature
subscribing to `order.plaecd` installs cleanly, passes its health check, and
never hears anything — and the person who notices is the merchant, weeks later.

### The shopper's credentials never leave the store

A mount ran the Feature's code in the store's process with the store's database
handle. A proxy makes an HTTP request and returns what comes back, and the
difference has to be kept honest:

- the store strips `Cookie`, `Authorization`, CSRF tokens and everything else the
  shopper carries;
- it asserts **who is asking** in a header it signs, and nothing else;
- it decides `anonymous` / `customer` / `staff` itself, because a service
  deciding whether a caller is staff would be the store trusting a third party
  about its own users;
- it forwards only the methods the manifest declared, so a read-only Feature
  cannot acquire a `DELETE` because nobody wrote a list;
- it refuses to return `Set-Cookie`, because a Feature's service must not be able
  to issue a session on the store's origin.

## Consequences

**A Feature is built once.** Not once per runtime — once. A Django service is
reachable from a Django store, a node store and an ASP.NET Core store equally,
because all three make HTTP requests. That is the scaling argument, and it is the
whole reason for the change.

**A fix ships when its author ships it**, not when every store gets round to
taking a version. The corollary is the real cost: an author can now break every
store at once, and a store cannot pin against that the way it pins a version.
Versioning the *configuration* does not version the service behind it.

**The store's database is out of reach.** No migrations, no reverse migrations,
no extension creation, no maintenance window, and no rollback that has to undo a
schema. Six of the eight defects phase 18 found were in that machinery, and none
of them can happen to a Feature delivered this way.

**Latency becomes a shopper-visible property.** A mounted Feature was a function
call; a proxied one is a network round trip on a request path. The store's own
timeout is short and a service that has gone away answers 502 rather than
hanging, but a Feature that was fast because it ran in-process will not be.

**Two architectures exist, and both are supported.** The sixteen existing
Features are unchanged, their manifests keep parsing, and `architecture` absent
means `in_process`. In-process delivery is not deprecated by this decision: it
remains the right answer for anything that genuinely needs to be inside the
store's transaction, and there is no way to be inside a transaction over HTTP.
What is deprecated is reaching for it *by default*.

**Data does not migrate itself.** A store on `subscriptions` 1.x has that
Feature's tables in its own database, and installing 2.0.0 does not move them.
That is why the proof of concept is a **major** version: the two are different
deployments of the same product, and moving a merchant between them is an
operational job rather than something a manifest can promise.

## What was rejected

**Deleting the in-process path.** The brief called this a pivot away from
in-process plugins, and building it that way would have been wrong: sixteen
Features are installed on real stores, and a delivery mechanism cannot be removed
faster than its slowest customer can migrate. It is additive, and the direction
is documented rather than enforced.

**A `register-webhooks` verb.** See above. The vocabulary is the compatibility
surface with three agents nobody redeploys on our schedule.

**Letting a Feature subscribe to arbitrary events.** A closed catalogue per store
means a typo is refused at install rather than discovered by its absence.

**Synchronous hooks that can veto.** Every event is past tense — `order.placed`,
not `order.placing`. A third party that can refuse an order is a checkout that
goes down when somebody else's server does.

**Forwarding the shopper's session.** It is the obvious way to make authentication
"just work" and it hands a credential the service could replay against the store.
The store asserts identity instead, and signs the assertion.
