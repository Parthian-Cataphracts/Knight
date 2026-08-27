# The node reference store

The smallest store that is **not** a Django application and can still take
delivery of a KNIGHT Feature.

It exists because of one sentence in
[`adr/0032`](../../docs/adr/0032-a-feature-declares-its-runtime.md) §4: a runtime
is not real until a store has received a Feature over it. Adding `node` to a
closed list in a validator would have been a promise. This is the thing behind
the name.

## What it demonstrates

That the delivery contract was never Django's. The step vocabulary is KNIGHT's —
`preflight`, `fetch`, `verify`, `install`, `migrate`, `configure`, `enable`,
`healthcheck` — and this store performs the same eight verbs the Django store
does. What differs is only what each verb *does*:

| | Django store | this store |
|---|---|---|
| `install` | unpacks a Python distribution | unpacks an npm package |
| `migrate` | `manage.py migrate <app_label>` | records the schema under the declared namespace |
| mount | adds a urlconf to the root urlconf | mounts an exported handler at a prefix |
| health | imports a dotted path and calls it | imports `module#export` and calls it |

Neither store decides *whether* to migrate, or where to mount, or what to load.
The job says, in the three neutral names `adr/0032` §3 settles: **namespace**,
**module**, **mount**.

## What it deliberately is not

**It is not a storefront.** There is no catalogue, no basket and no checkout. It
answers "did the Feature install, and can a request reach it", which is the only
question it exists to answer.

**It is not highly available.** `work` claims jobs one at a time until there are
none and then exits. A real store runs it on a schedule; there is no daemon, no
queue and no concurrency, because a command exits with a status and can be run by
hand during an incident.

## It does claim jobs over HTTP, since phase 20

It did not until then, and the reasoning was that the transport is identical to
the Django store's — same endpoints, same token, same reporting — so duplicating
it here would prove nothing about runtime neutrality.

That reasoning was sound and the conclusion was wrong. A store that never asks
KNIGHT for work is a store nobody has ever proved KNIGHT can hand work to, and
when phase 20 finally tried, KNIGHT could not plan against this store at all:
compatibility was decided on Python and Django versions this store has no way to
report, so every Feature was refused. Nothing noticed for three phases, because
nothing had ever asked.

```bash
KNIGHT_CLIENT_ID=… KNIGHT_CLIENT_SECRET=… npm run connect
```

Handshake, then one heartbeat reporting `{"name": "node", "node": "…"}`. The name
is the part that matters: KNIGHT decides from it which compatibility checks even
apply, and a store that omits it is refused rather than assumed to be Django.

```bash
KNIGHT_CLIENT_ID=… KNIGHT_CLIENT_SECRET=… npm run work
```

Claim, run, report each step as it finishes, report the outcome. A step that
fails is reported as a failure with its own code — `digest.mismatch` and
`install.failed` need different people woken up — and a job that failed silently
is a Feature a merchant has paid for and does not have.

**It has no dependencies.** Not even for reading a zip. A reference
implementation exists to show a team what they have to build, and "add this
package" shows nothing to a team writing their store in Go.

## Running it

Build a signed artifact and a job payload the way KNIGHT would, then run it:

```bash
python features/tools/knight_package.py build features/knight-feature-node-conformance --dist /tmp/knight
```

The job payload's shape is the one `StoreJobEndpoints` produces; there is a
worked example in [`test/delivery.test.js`](test/delivery.test.js), which builds
one against a real signature.

```bash
KNIGHT_FEATURE_ROOT=/tmp/knight/features KNIGHT_TRUSTED_KEYS='{"dev-key":"<base64 SPKI DER>"}' npm run apply-job -- /tmp/knight/job.json
```

Then serve what was installed:

```bash
KNIGHT_FEATURE_ROOT=/tmp/knight/features npm start
```

```bash
curl http://localhost:8100/conformance/
```

```bash
curl http://localhost:8100/health
```

## Testing it

```bash
npm test
```

Fourteen tests, and they run against the **real** artifact — built by
`knight_package.py` from `features/knight-feature-node-conformance`, verified
against a real ECDSA P-256 signature, unpacked by this store's own reader. A
fixture zip written by hand would prove this store can read a zip somebody wrote
for it; that proves it can read what KNIGHT actually publishes.

The refusals are the half worth reading: a package built for another runtime is
refused in `preflight` before anything is downloaded, a job with no runtime named
is treated as Django and therefore refused, a valid signature by an untrusted key
is rejected, and an archive entry pointing outside its package takes the whole
archive down rather than being skipped.

## Configuration

| Variable | Meaning |
|---|---|
| `KNIGHT_FEATURE_ROOT` | where installed packages and `installed.json` live |
| `KNIGHT_WORKSPACE` | scratch space for downloads |
| `KNIGHT_TRUSTED_KEYS` | `{"keyId": "base64 SPKI DER"}` — configuration, never anything a payload carries |
| `KNIGHT_MAX_ARTIFACT_BYTES` | a ceiling, because a download with no limit is a disk with no floor |
| `PORT` | defaults to 8100, clear of the Django store's 8000 |
