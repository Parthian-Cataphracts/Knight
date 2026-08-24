# Installing KNIGHT on a server

Status: **authoritative**. For running KNIGHT on a laptop, see
[`development.md`](development.md); for the shape of a deployment and the
decisions behind it, [`deployment.md`](deployment.md).

## 1. One command

On a fresh Ubuntu 22.04+ or Debian 12+ server, as root:

```bash
bash <(curl -Ls https://raw.githubusercontent.com/Parthian-Cataphracts/Knight/main/install.sh)
```

Point the domain's DNS A record at the server first, or the certificate cannot
be issued and the install finishes on plain HTTP.

Use that form rather than `curl … | bash`. The installer asks questions, and a
pipe leaves it reading end-of-file on every one of them; it checks for this and
refuses rather than installing on blank answers.

Every question is asked at the start, so the rest — a package install, two
builds, migrations and a certificate, five to ten minutes on a small machine —
runs unattended.

## 2. What you are asked

| | |
|---|---|
| **Domain** | One hostname serves the dashboard, the API, the realtime hub and the artifact downloads agents fetch |
| **Certificate email** | Where Let's Encrypt sends expiry notices |
| **Administrator email and password** | The first account. It holds `SuperAdmin`, so its first sign-in enrols a second factor |
| **Database name and role** | Defaults `knight`/`knight`. Changed if a role of that name already exists and is somebody else's |
| **Artifact signing public key** | Optional. Without it KNIGHT runs, and publishing a Feature version is unavailable until `knightctl signing-key` sets one |
| **SMTP** | Optional. Without it, a new administrator gets a one-time password instead of an activation link |

## 3. One hostname, not two

`deployment.md` §4 describes a dashboard host and a separate `api.` host. The
installer deploys one hostname, with nginx routing by path:

```
https://knight.example.com/            the dashboard bundle
                          /api/v1/     the control-plane and ingestion API
                          /hubs/       the realtime channel (WebSocket)
                          /artifacts/  signed Feature packages, fetched by agents
                          /health/     liveness and readiness
```

One DNS record, one certificate, and — because dashboard and API share an
origin — no cross-origin request to get wrong. The dashboard bundle addresses
all of them relatively, so it carries no hostname and no scheme, and
`knightctl domain` can move a deployment without rebuilding anything.

The two-hostname topology remains valid and is what to reach for when the API
needs a stable address independent of the dashboard's. It is not what this
installer builds.

## 4. What is created, exactly

```
/opt/knight/
├── src/            the checkout this deployment was built from
├── app/api/        the published API
├── app/bootstrap/  the migration and account tool
├── dashboard/      the built bundle nginx serves
├── artifacts/      the Feature package store
├── backups/        nightly database dumps
├── storage/        uploaded files
├── state/          data-protection keys
├── redis/          KNIGHT's own Redis instance
├── toolchain/      a private .NET and Node, only if the host had none suitable
└── knight.env      configuration and secrets, owner-only
```

| Elsewhere | What |
|---|---|
| `knight` system user | No login shell. Owns everything above and runs both services |
| `/etc/systemd/system/knight-api.service` | The control plane, on `127.0.0.1` only |
| `/etc/systemd/system/knight-redis.service` | KNIGHT's own Redis, on `127.0.0.1` only |
| `/etc/systemd/system/knight-backup.{service,timer}` | Nightly dump at 02:30 |
| `/etc/nginx/sites-available/knight` + its symlink | The one site |
| `/etc/nginx/conf.d/knight-shared.conf` | The two directives that have to be in the `http` context, both prefixed `knight_` |
| `/usr/local/bin/knightctl` | The management tool |
| PostgreSQL | One role and one database |

## 5. Sharing a server with other applications

A control plane is small, and putting it on a machine that already runs
something else is the normal case rather than the exception. The installer is
written for that, and these are the promises it keeps.

**It never replaces a toolchain another application depends on.** A host .NET 10
SDK or Node 20+ is used where it exists. Where it does not, a private copy goes
under `/opt/knight/toolchain` and is never symlinked onto `PATH`. Installing a
second .NET into `/usr/share/dotnet` is how an unrelated application discovers
its runtime has moved underneath it.

**It picks free ports.** The API and Redis take the first free port from 5080
and 6380, and both listen on `127.0.0.1` only. Nothing assumes 5000 or 6379.

**Redis is KNIGHT's own instance, not a shared one.** A shared server means a
shared `FLUSHALL`, a shared eviction policy and a shared memory ceiling — three
ways for two applications to break each other. KNIGHT's instance has its own
password, its own data directory, and a 256MB ceiling with `noeviction`, so a
fault here cannot take the memory the rest of the machine is relying on. If the
`redis-server` package has to be installed, the stock instance on 6379 is
stopped — but only when the installer is what put it there. One that was already
running is left exactly as it was found.

**PostgreSQL gets one role and one database.** Nothing else on the server is
read or altered, and `pg_hba.conf` is not edited. If a role of the chosen name
already exists and the installer did not create it, it stops and asks rather
than resetting a password another application is authenticating with.

**nginx gets one site and one `conf.d` file.** `nginx.conf` is not edited, the
default site is not disabled, and no other site is touched. The `conf.d` file
holds only the two directives that have to live in the `http` context — a
WebSocket upgrade map and a rate-limit zone — both named `knight_*`, because a
duplicate map variable or zone name makes nginx refuse to load *every* site on
the machine, not only this one.

**The service is confined.** `knight-api` runs as `knight` under
`ProtectSystem=strict` with `NoNewPrivileges`, and can write to exactly three
directories: `artifacts`, `storage` and `state`. Everything else on the
filesystem, including the rest of `/opt/knight`, is read-only to it.

**Uninstalling is held to the same rule.** `knightctl uninstall` removes
KNIGHT's units, its site, its `conf.d` file and — if asked — its directory and
its database. Shared packages are left installed, other nginx sites keep being
served, other databases are untouched, and the TLS certificate is left in place
because another vhost may share it.

## 6. Unattended installs

```bash
KNIGHT_ASSUME_YES=1 \
KNIGHT_DOMAIN=knight.example.com \
KNIGHT_SSL_EMAIL=ops@example.com \
KNIGHT_ADMIN_EMAIL=admin@example.com \
KNIGHT_ADMIN_PASSWORD='…' \
bash install.sh
```

Also read when set: `KNIGHT_DB_NAME`, `KNIGHT_DB_USER`,
`KNIGHT_SIGNING_KEY_ID`, `KNIGHT_SIGNING_PUBLIC_KEY`, `KNIGHT_SMTP_HOST`,
`KNIGHT_SMTP_PORT`, `KNIGHT_SMTP_FROM`, `KNIGHT_SMTP_USER`,
`KNIGHT_SMTP_PASSWORD`, `KNIGHT_REPO_URL`, `KNIGHT_REPO_REF`.

A password in the environment is visible to anything that can read the process
environment, which is why the interactive path — where it is typed, masked, and
never becomes an argument — is the default. Use this form for a provisioning
system that already handles secrets, not to save typing.

## 7. Re-running it

Safe, and the way to rebuild after changing something by hand. The database, the
artifacts and the backups are kept, no question is asked twice, and no second
administrator is created.

So are the secrets, and for a different reason in each case:

- Rotating `Jwt__SigningKey` would sign every administrator out.
- Rotating `Stores__IntegrationSigningKey` would invalidate the entitlement
  payloads every connected store is currently holding.
- **Every** artifact signing key is carried across, not only the active one. A
  retired key still has to verify the versions it signed, so dropping one would
  make already-published Feature versions unverifiable.

An environment variable named on the run wins over the stored answer, so
`KNIGHT_DOMAIN=…` on a re-install moves the deployment rather than being
ignored. The branch the deployment tracks is recorded too, so a re-install and
`knightctl update` follow the branch it was installed from rather than the
repository's default one.

For an ordinary upgrade use `knightctl update`, which takes a backup before it
migrates anything.

## 8. After the install

1. Open `https://<domain>` and sign in as the administrator you created.
2. It holds `SuperAdmin`, so it will ask you to enrol an authenticator app
   before it can reach anything else ([`authentication.md`](authentication.md) §1).
3. Then, in the dashboard: create a customer, register their store, and issue
   the store a credential. **The secret is shown exactly once.**
4. On the store, put that credential in its environment and run
   `manage.py knight_register`. The store reports `Pending` until it has proven
   it owns its domain ([`adr/0021`](adr/0021-domain-verification-before-connected.md)).
5. Start domain verification from the dashboard, publish the token the store is
   given, and verify. The store is now `Connected`.

[`store-integration.md`](store-integration.md) is the full contract, and
[`phase-3-verification.md`](phase-3-verification.md) walks the whole sequence
with the exact commands.

## 9. knightctl

Run it with no arguments for a menu, or name a command.

```
knightctl status              what is running, and whether it is healthy
knightctl doctor              run every check and report what is wrong
knightctl logs [api|redis|backup|nginx]
knightctl start|stop|restart
knightctl update              pull, rebuild, migrate and restart
knightctl backup              take a dump now
knightctl restore [dump]      restore one over the control-plane database
knightctl admin               create an administrator
knightctl domain <hostname>   move this deployment to another hostname
knightctl signing-key         set the artifact signing public key
knightctl config              show the configuration, secrets elided
knightctl uninstall           remove KNIGHT and nothing else
```

`update` fetches, rebuilds both halves, **takes a backup**, applies migrations
and restarts. The backup comes first because a migration is the one thing an
update does that a restart cannot undo. A build that fails leaves the running
deployment untouched.

`domain` rewrites the nginx `server_name`, requests a certificate for the new
hostname and updates the three settings that name it. Nothing is rebuilt: the
dashboard bundle carries no hostname.

## 10. Backups

A dump is taken nightly at 02:30 into `/opt/knight/backups`, with a manifest
recording its SHA-256; a restore refuses a dump whose checksum does not match
([`adr/0027`](adr/0027-the-restore-drill-is-the-backup-test.md)).

**A backup on the same machine is not a backup.** Copying `/opt/knight/backups`
somewhere else is deliberately not automated, because where it should go is a
decision about custody rather than a default. Until that copy exists, one failed
disk takes the dumps with it.

A dump holds every customer, every credential hash and the whole audit trail.
Treat the directory, and anywhere it is copied to, accordingly.

## 11. Configuration

Settings live in `/opt/knight/knight.env`, which is the systemd
`EnvironmentFile` for `knight-api` and is owner-only. Names are standard .NET
configuration paths with `__` for `:` — `Jwt__SigningKey` is `Jwt:SigningKey`.
`knightctl config` prints them with secrets elided.

To change one by hand, edit the file and `knightctl restart`. Values are single
quoted, which systemd and the shell read identically.

Two of them are load-bearing and are generated once, at install:

- `Jwt__SigningKey` signs administrator tokens.
- `Stores__IntegrationSigningKey` signs the entitlement payloads stores cache
  and trust. It is deliberately a *different* key, so one leak does not
  compromise both ([`authentication.md`](authentication.md) §5).

Changing either invalidates what it signed, so neither is regenerated by a
re-install.

## 12. What this installer does not do

- **It does not install the agent.** The agent runs on the servers that host
  customer stores, which are other machines. See [`../agent/README.md`](../agent/README.md).
- **It does not provision stores.** A store is an independent Django
  application with its own deployment ([`store-provisioning.md`](store-provisioning.md)).
- **It does not sign Feature packages.** Signing is offline, by
  `features/tools/knight_package.py`, wherever the private key lives. The
  installer only ever takes the public half.
- **It does not copy backups off the machine.**

## 13. When something is wrong

```bash
knightctl doctor                       every check, in one screen
journalctl -u knight-api -n 100        what the API said
nginx -t                               whether nginx is happy
```

| Symptom | Cause |
|---|---|
| Install ends on plain HTTP | DNS was not pointing here yet. Fix the A record, then `certbot --nginx -d <domain> --redirect && knightctl domain <domain>` |
| API will not start, log names Redis | `knight-redis` is down; `systemctl status knight-redis`. Outside Development KNIGHT refuses to start without it ([`adr/0020`](adr/0020-store-ingestion-authentication.md)) |
| API will not start, log names a signing key | `Jwt__SigningKey` or `Stores__IntegrationSigningKey` is missing from `knight.env`. Both are refused empty in Production, at startup, on purpose |
| Dashboard loads, every call is 401 | The clock. Tokens carry 30 seconds of skew tolerance; check `timedatectl` |
| Realtime never connects | The `/hubs/` location or its two upgrade headers are missing from the nginx site |
| `nginx -t` fails after install | Another application declares a map variable or `limit_req_zone` of the same name. Both of KNIGHT's are prefixed `knight_`; rename the other one |
