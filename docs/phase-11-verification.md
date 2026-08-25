# Phase 11 — verification

Status: **done**, 2026-08-24. Everything below was carried out against real
servers running systemd, PostgreSQL and nginx — not a test host, and not a
dry run.

## 1. What was built

| TODO item | Where it lives |
|---|---|
| One-command server install | [`install.sh`](../install.sh) |
| Management tool | [`knightctl.sh`](../knightctl.sh) → `/usr/local/bin/knightctl` |
| What it creates, and what it refuses to touch | [`installation.md`](installation.md) |
| Correct configuration reference | [`deployment.md`](deployment.md) §5 — the old one listed keys that do not exist |
| Nightly backup scheduling | `knight-backup.timer`, the item `deployment.md` §10 had left open |
| Shell scripts in CI | `.github/workflows/backend.yml`, job **Shell scripts** |

## 2. How to verify it

Two servers, because half of these defects only exist on the second install.

### A fresh server

Ubuntu 22.04+ or Debian 12+, root, DNS A record already pointing at it:

```bash
bash <(curl -Ls https://raw.githubusercontent.com/Parthian-Cataphracts/Knight/main/install.sh)
```

Answer: the domain, an email for certificate notices, an administrator email
and password. Press Enter past the signing key and SMTP. Five to ten minutes.

Then, in a browser at `https://<domain>`:

1. Sign in as the administrator. It must ask you to enrol an authenticator app
   before it will show you anything — that is `SuperAdmin` behaving correctly,
   not a broken screen.
2. Enrol, sign in again, and walk Customers → Stores → Plans.

From a shell on the server:

```bash
knightctl status      # every service, the certificate expiry, the backup count
knightctl doctor      # twelve checks; expect two warnings on a new install:
                      #   no backup yet, and no artifact signing key
curl -s https://<domain>/health/ready
```

Expect `{"status":"Healthy","checks":[{"name":"postgresql","status":"Healthy",…`

### The same server, a second time

This is the half that matters, and the half that was broken:

```bash
knightctl signing-key            # any base64 DER public key, id "primary"
knightctl backup                 # a dump and a manifest in /opt/knight/backups
bash <(curl -Ls https://raw.githubusercontent.com/Parthian-Cataphracts/Knight/main/install.sh)
echo "exit=$?"                   # must be 0
```

The second install must:

- get past **Source** (a `dubious ownership` error here is the bug that was
  fixed — the checkout is owned by `knight` and git is running as root),
- say *"Signing key 'primary' is already configured and is kept"*,
- say *"Administrator accounts already exist; not creating another"*,
- leave `Jwt__SigningKey`, `Stores__IntegrationSigningKey`,
  `KNIGHT_DB_PASSWORD` and `KNIGHT_REDIS_PASSWORD` byte-identical.

Prove the last one:

```bash
grep -E '^(Jwt__SigningKey|Stores__IntegrationSigningKey|KNIGHT_DB_PASSWORD|KNIGHT_REDIS_PASSWORD)=' \
  /opt/knight/knight.env | sha256sum       # same before and after
grep '^FeatureArtifacts__' /opt/knight/knight.env   # the key is still there
```

### Every knightctl command

```bash
knightctl update                     # "Already at <sha>. Nothing to do."
knightctl restart                    # "Restarted, and reporting ready."
knightctl admin                      # a second administrator
knightctl domain other.example.com   # rewrites nginx, reissues, waits for ready
knightctl restore                    # pick the dump; then sign in again
```

`restore` is the one worth doing deliberately: it drops and recreates the
database. It must finish with *"Restored, and the API is reporting ready"* and
the site must answer immediately afterwards. A `permission denied to create
database` here is the defect that left a server with no database at all.

### A server that already runs something else

The whole point. On a machine with another nginx site and another PostgreSQL
role:

```bash
ss -ltnH | awk '{print $4}' | sort -u   # only :80 is public; 5080, 6380, 5432 on 127.0.0.1
ls /etc/nginx/sites-enabled/            # the other site is still enabled
ls /etc/nginx/conf.d/                   # one knight-shared.conf, both names prefixed
curl -H "Host: the-other-app" http://127.0.0.1/     # still 200
```

Then install a second time with `KNIGHT_DB_USER` set to the *other*
application's role. It must stop and refuse rather than reset that role's
password, and the other application must still be able to log in.

Finally:

```bash
knightctl uninstall     # answer yes to all four
```

The other nginx site must still answer 200, the other PostgreSQL role must
still log in, and `nginx`, `postgresql`, `redis-server` and `certbot` must all
still be installed.

## 3. What the runs actually showed

Three installs on one container and two on another, all Ubuntu 24.04 with
systemd, PostgreSQL 16, nginx and a real Redis.

| | |
|---|---|
| Fresh install, from the published one-liner | 200 on `/`, `{"status":"Healthy"}` on `/health/ready`, sign-in returning `mfa_enrollment_required` with a token issued for `knight-control-plane`, wrong password returning 401 |
| Second install | Kept every secret byte-for-byte, kept the signing key, created no second administrator, exit 0 |
| Ports | `127.0.0.1:5080` API, `127.0.0.1:6380` Redis, `127.0.0.1:5432` PostgreSQL. Only nginx on `:80` |
| The neighbour | nginx's default site still served 200 throughout, including after uninstall |
| PostgreSQL | Exactly one database and one role added; a second install aimed at another application's role stopped and refused |
| Backup | `knight-backup.service` under `ProtectSystem=strict` wrote a 128KB dump and a manifest with its SHA-256 |
| Restore | Database dropped and recreated with the right owner, 51 tables restored, API healthy afterwards |
| Restore drill | Unchanged and still passing: 51 tables, 198 rows, 14 migrations, 88 constraints, 154 indexes |
| Uninstall | Units, site, `conf.d` file, directory, user, database and role gone; shared packages and the other role untouched |

## 4. Defects found by running it, and fixed

Every one of these needed a real server. Five of the six needed a *second*
install on that server.

1. **Nothing read the reverse proxy's forwarded headers.** Every request
   appeared to come from `127.0.0.1`, so the sign-in and ingestion rate limiters
   gave the whole internet one shared bucket and every audit row recorded the
   proxy's address. Fixed in `Program.cs`, covered by `ReverseProxyTests`.
2. **Re-installs and `knightctl update` failed outright** — `chown -R knight`
   plus git running as root is a `dubious ownership` refusal.
3. **A re-install silently dropped the artifact signing keys**, which would have
   made already-published Feature versions unverifiable.
4. **`knight-restore.sh` needed a privilege the application role does not have.**
   A real restore dropped the database and could not recreate it. The CI drill
   never showed it, because there the role owns the cluster.
5. **`knightctl` reported success before the API was serving**, sending the
   operator to a 502 on five different commands.
6. **The installer's exit status** was whatever its last statement happened to
   return.

## 5. Not done

- Certificate issuance against real DNS. Everything else was exercised; a
  container has no resolvable domain, so `certbot` was only ever seen failing
  gracefully and reporting what to run once DNS is in place.
- Container images and the deploy stages of [`deployment.md`](deployment.md) §8,
  which still wait on a hosting-platform decision — and which the server install
  no longer waits on.
- An offsite copy of the nightly dumps. The timer writes them to the same
  machine, and the installer says so rather than implying otherwise.
- Installers for the machines that host stores: the agent, and a Django store.
