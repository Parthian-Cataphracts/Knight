# Phase 27 — how far it got, and what it is waiting for

Phase 27's exit criterion: **KNIGHT, the reference store and one service deploy
from CI to a real host, with TLS, scheduled backups going offsite, and a
rehearsed way back.** Its gate: **a deploy, and a restore from the offsite copy
onto a clean machine.**

**The gate has not been run, and it cannot be from here.** It needs a host, a
domain and a place to put backups, and all three are decisions only the product
owner can make — they are three of the five in [`roadmap.md`](roadmap.md) §7.
Nothing in this phase pretends otherwise: what was built is the half the roadmap
itself calls unblocked, and the blocked half is listed at the bottom with what
each item is waiting for.

---

## 1. What was built

| | |
|---|---|
| **[`install-agent.sh`](../install-agent.sh)** | Puts the agent on a server that hosts stores: a system user, a virtualenv of its own, enrolment, a hardened unit |
| **[`install-store.sh`](../install-store.sh)** | Puts a Django store on such a server, laid out where the agent looks for it, served through a socket |
| **A reload that drops nothing** | The store's unit is socket-activated; `systemctl reload` swaps workers without closing the listening socket |
| **[`knight-offsite.sh`](../infrastructure/scripts/knight-offsite.sh)** | Copies the newest dumps to rsync, S3 or a mount, verifying each one before and after |
| **An offsite timer** | Written by the installer, enabled only once somebody has chosen a destination |

### The three things worth explaining

**A re-run must never un-enrol a server.** `install-agent.sh` keeps an existing
credential and says so. The token that enrolled a machine was one-time and
nobody has it any more, so an installer that re-enrolled on every run would turn
a routine upgrade into a server nothing can reach.

**A reload is not a restart.** The store's socket belongs to systemd and outlives
the service, so reloading starts new gunicorn workers and lets the old ones
finish what they are holding. A request that arrived a millisecond before a
deploy is answered; one that arrives during the swap waits in the socket's
backlog. That was carried from phase 3.5 as *"the installer writes a unit and
restarting it drops whatever was in flight"*, and it is the difference between
deploying at three in the morning and deploying at three in the afternoon.

**The offsite timer is written and not enabled.** Where a database holding every
customer record may be copied to is a custody decision, and an installer that
picked one would be making it on somebody's behalf. Until `KNIGHT_OFFSITE_TARGET`
is set the timer exists, does nothing, and the installer says plainly that every
backup lives on one disk — which is the honest arrangement, as against a nightly
job that silently copies nothing.

---

## 2. What building it found

**A `file://` destination looks finished and often is not.** A directory on the
same disk is a second copy of a file, not a second place. The script says so on
every run rather than in a comment nobody reads.

**A dump must be verified before it is sent, not only after.** Shipping a
corrupt dump offsite turns one bad file into two, and the second one is the copy
somebody reaches for. `knight-offsite.sh` refuses a dump that does not match its
own manifest and exits non-zero, so the timer fails loudly.

**The store installer nearly wrote a `DATABASE_URL` nothing reads.** The
reference store's settings read `STORE_DB_NAME`, `STORE_DB_USER` and the rest.
An installer writing a URL would have produced a store that starts, silently
connects to the default database, and is wrong in a way nobody notices until two
shops share one.

---

## 3. How to test it

The parts that do not need a host can be exercised now.

### The offsite copy

```bash
mkdir -p /tmp/backups /tmp/offsite
KNIGHT_DB=knight infrastructure/scripts/knight-backup.sh /tmp/backups

KNIGHT_OFFSITE_TARGET=file:///tmp/offsite \
  infrastructure/scripts/knight-offsite.sh /tmp/backups
```

Expect the dump and its manifest in `/tmp/offsite`, and the note that a
directory is offsite only if it is a mount. Then prove the refusals:

```bash
echo corrupt >> /tmp/backups/knight-knight-*.dump
KNIGHT_OFFSITE_TARGET=file:///tmp/offsite infrastructure/scripts/knight-offsite.sh /tmp/backups
# offsite: … does not match its manifest. It was not copied.   (exit 1)

KNIGHT_OFFSITE_TARGET= infrastructure/scripts/knight-offsite.sh /tmp/backups
# offsite: KNIGHT_OFFSITE_TARGET is not set. Nothing was copied, and nothing
#          pretended to be.                                     (exit 1)
```

Both were run and both behave as printed.

### The installers

They write systemd units and create users, so they want a throwaway Linux VM or
container — running them on a workstation is not the test they are for. In CI
they are syntax-checked and shellchecked with the others, which is what catches
the class of mistake that only appears on the machine being installed.

```bash
bash -n install-agent.sh install-store.sh
shellcheck -S warning install-agent.sh install-store.sh
```

---

## 4. What this phase is waiting for, and on whom

| Item | Waiting for |
|---|---|
| Docker images and the deploy stages of §8 | **the hosting decision** |
| The installer against a real cloud VM with real DNS, and TLS issuance | **a VM and a domain** |
| Provisioning automation — machine, instance, DNS, TLS | **the same hosting decision** |
| The offsite copy actually running nightly | **a custody decision**: where the dumps may go |
| The gate — a deploy, and a restore from the offsite copy onto a clean machine | **all of the above** |

Two items on the phase's list are not blocked and were still not done, and
saying which is the point of this section:

- **Signed agent releases and a self-update path**, carried from phase 4. An
  agent that cannot update itself is a fleet somebody updates by hand, and the
  new installer makes that one command per machine rather than a page of them —
  better, and not the same as solved.
- **The store installer has never installed the node or .NET stores.** It is a
  Django installer and says so; the other two runtimes deploy by their own means.
