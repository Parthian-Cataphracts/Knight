# Store-integration conformance checker

The KNIGHT↔store contract as something you can run.

A customer store may be written in anything. What makes it a store is not its
framework but a handful of HTTP calls, so this is the definition a new
integration is finished against: run it until it is green.

```bash
python stores/conformance/knight_conformance.py selftest
```

`selftest` needs nothing running. It reproduces the two signed strings from
[`docs/contracts/store-integration.samples.json`](../../docs/contracts/store-integration.samples.json),
which is the byte-for-byte authority KNIGHT and the reference store are both
already tested against. It runs in CI on every push, because a checker that has
drifted from the contract would report confident, wrong verdicts about somebody
else's store — which is worse than not checking at all.

```bash
python stores/conformance/knight_conformance.py check \
  --knight-url   https://knight.example.com \
  --client-id    <from the dashboard> \
  --client-secret <shown exactly once when the credential was issued> \
  --environment  Production \
  --store-url    https://cafe1.ir
```

`check` performs a real handshake with real credentials, so it tests a
deployment rather than a mock. Every assertion it makes is one an operator would
otherwise be making by hand.

## What it checks

**Against KNIGHT** — works as soon as your service can make an HTTP request:

- the credentials are accepted, and the response carries every field the
  contract requires
- the store token is short-lived, because it is the one credential in this
  system that cannot be rotated
- a handshake replaying the same nonce is refused
- a heartbeat is accepted with the token and refused without it
- the entitlement set is for this store, and its signature verifies with the key
  the handshake returned

**Against your store** — the half somebody has to write, and where a new
integration usually fails:

- `GET /api/knight/health` refuses an unsigned request
- it accepts a correctly signed one and answers with a valid health body
- it refuses a signature made over a different path
- it refuses a correctly signed request that is an hour old
- `/.well-known/knight-domain-verification` serves the token KNIGHT issued

Without `--store-url` the second half is skipped and says so. Until those
endpoints work the store will never leave `Pending`, whatever else it reports.

## Reading the output

`PASS` and `FAIL` are about the contract. `WARN` is about this deployment: a
store that is still `Pending` has not done anything wrong, it has not finished
proving its domain yet.

The run reports everything it finds rather than stopping at the first problem,
and exits non-zero if anything failed.

See [`docs/connecting-a-store.md`](../../docs/connecting-a-store.md) for what
each of these calls is and how to implement them.
