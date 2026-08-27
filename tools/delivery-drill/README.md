# The delivery drill

The whole journey a customer's Feature takes, as one command:

```bash
python tools/delivery-drill/drill.py
```

Publish → onboard → connect → install → upgrade → roll back → withdraw, against a
real KNIGHT and a real store, asserting at every step and exiting non-zero on the
first thing that is not true.

## Why it exists

Phase 18 walked this journey by hand and found **eight defects**, six of which
made delivery impossible and had been that way for six phases. Every one of them
now has a unit or integration test — and not one of those tests would find the
*next* one. They check the code each defect happened to be in; what the eight had
in common was the path.

Nothing else in this repository runs that path. A Feature is authored with
`knight_install_local` and a `pip install`, which is the right tool for authoring
and exists precisely to bypass the plan, the compatibility check, the artifact,
the job, the migrate subprocess and the rollback.

The drill's first run, on the first rollback across two versions whose migrations
actually differed, found a ninth: the rollback restored the old package *before*
reversing its migrations, and Django can only unapply a migration whose file it
can still see. The code rolled back and the database did not.

## What it proves

| Step | What is asserted |
|---|---|
| Publish | Real catalogue packages still build, sign and publish |
| Onboard | Customer, store, credential, plan, subscription, entitlement — all through the API |
| Connect | **A connected store can be planned against** — it reported its runtime and its database |
| Install | Every Feature lands; a pinned version is honoured; the newest is taken when none is named; the migrations ran |
| Upgrade | An upgrade with no version named queues a job, applies the migration, and keeps the rows |
| Roll back | KNIGHT records the version restored, the rows survive, **and the schema is reversed** |
| Withdraw | Revoking an entitlement disables without anybody queueing a job, the data stays, the store agrees |

## What it is not

Not a unit test and not a substitute for one. It is slow, it needs a database and
two processes, and when it fails it names the step rather than the line. That is
the right trade for a path whose failures are invisible to everything else.

## Its own Feature

[`versions/1.0.0`](versions/1.0.0) and [`versions/1.1.0`](versions/1.1.0) are the
Feature the drill moves up and down. They are two source trees rather than one
with a build-time switch, so a reader can diff them and see the whole difference:
**1.1.0 has a `note` column and 1.0.0 does not.**

That column is the point. It gives the upgrade something to apply and the
rollback something to reverse, so "the upgrade did nothing" and "the rollback did
nothing" are both visible in the schema rather than only in a version number.
Phase 18's rollback moved between two versions whose migrations were identical
and could see neither.

It is **not in the commercial catalogue**. The drill creates its identity through
the API at run time, so the sellable catalogue stays free of test fixtures — the
same separation `node-conformance` keeps for the node runtime.

## Running it

It needs a KNIGHT with an administrator that has a password and an enrolled
second factor, the signing key KNIGHT trusts, and somewhere to put artifacts.

```bash
export KNIGHT_BASE_URL=http://localhost:5008
export KNIGHT_ADMIN_EMAIL=admin@knight.dev
export KNIGHT_ADMIN_PASSWORD='…'
export KNIGHT_ADMIN_TOTP=BASE32SECRET
export KNIGHT_SIGNING_KEY='…'        # private half, base64 PKCS#8
export KNIGHT_PUBLIC_KEY='…'         # public half, so the store can verify
export KNIGHT_ARTIFACT_ROOT=/tmp/knight-artifacts
python tools/delivery-drill/drill.py
```

Everything it creates is named with a run id, and it makes **its own store
database** each run. That is deliberate: the first version reused one database
and the second run failed on a `NOT NULL` violation, because the table still had
1.1.0's column while the package on disk was 1.0.0's. A drill telling the truth
about its own environment rather than about the product is the least useful kind
of red.

A failed run leaves its database and its feature root behind, so there is
something to look at.
