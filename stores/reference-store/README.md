# KNIGHT reference store

An independent Django application that KNIGHT manages. It exists for two
reasons: it is the integration-test target for the KNIGHT↔Store contract, and it
is the worked example a real store is built from.

It is a **customer store**, not part of the control plane. It has its own
database, its own deployment, and its own business domain, and KNIGHT never
connects to any of them ([`docs/README.md`](../../docs/README.md), rules 1 and 3).

```
reference-store/
├── apps/shop/              business domain — never imports the integration layer
│                           beyond knight_integration.features
└── knight_integration/     everything KNIGHT-related, and nothing business-related
    ├── conf.py             settings + validation
    ├── client.py           HTTP client (timeouts, bounded retry, 401 re-handshake)
    ├── auth.py             credential handling and the store-token cache
    ├── features/           entitlement cache, signature verification, the façade
    ├── errors/             exception middleware → scrubbed, batched reporting
    ├── events/             deployment and lifecycle reporting
    ├── health/             the /api/knight/* surface KNIGHT calls
    └── management/commands knight_register, knight_sync_features,
                            knight_heartbeat, knight_selftest
```

The layering rule is enforced by a test, not by convention:
`knight_integration/tests/test_boundaries.py` fails if a business module reaches
past the feature façade, or if the integration layer imports a business model.

## Running it

Needs Python 3.12+ and a PostgreSQL this store may create tables in. See
[`docs/development.md`](../../docs/development.md) for a PostgreSQL that needs no
container runtime.

```bash
cd stores/reference-store
python -m venv .venv
.venv/Scripts/python -m pip install -r requirements.txt   # POSIX: .venv/bin/python

cp .env.example .env      # then fill it in — see below
.venv/Scripts/python manage.py migrate
.venv/Scripts/python manage.py runserver 127.0.0.1:8000
```

The store starts and serves shoppers whether or not KNIGHT is reachable, and
whether or not it has credentials at all. Without them it reports nothing and
says so once, at startup.

## Connecting it to KNIGHT

1. In the dashboard, create a customer, register a store, and issue a credential.
   **The secret is shown once.** Put it in this store's environment — never in
   the repository.
2. `manage.py knight_register` performs the handshake. It will report
   `Pending`, because the domain has not been proven yet.
3. Start domain verification for the store in KNIGHT, put the token it issues in
   `KNIGHT_DOMAIN_VERIFICATION_TOKEN`, restart the store, and verify from the
   dashboard. The store is now `Connected`
   ([`adr/0021`](../../docs/adr/0021-domain-verification-before-connected.md)).
4. `manage.py knight_selftest` exercises every step and says which one failed if
   one does. It is the first thing to run when something is wrong.

Schedule `manage.py knight_heartbeat --quiet` and
`manage.py knight_sync_features` from cron or a systemd timer, at the intervals
the handshake response reports.

## Configuration

Every value is read from the environment; see `.env.example` for the full list
and [`docs/store-integration.md`](../../docs/store-integration.md) §7 for what
each one means.

## What it demonstrates

- **Entitlement is enforced server-side, from a signed payload.** `/loyalty/`
  answers `402` when the capability is not paid for, and `503` when it is paid
  for but not installed — those are different problems with different owners.
- **A KNIGHT outage never becomes a shopper's problem.** Error reporting is
  queued and drained on a background thread, the queue is bounded, and the
  entitlement cache falls back to the last known good set for a bounded grace
  period and then to the minimum safe set. It never fails open on a paid
  capability and never takes the storefront down.
- **The health endpoint is authenticated.** It names versions, dependencies and
  installed features, which is diagnostics for an operator and reconnaissance for
  everyone else.
