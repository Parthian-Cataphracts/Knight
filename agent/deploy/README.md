# Hardened agent deployment

Least-privilege deployment for the KNIGHT agent (hardening backlog P2). Two
independent confinements — the systemd sandbox and an AppArmor profile — over an
unprivileged, dedicated user. The agent needs very little (read `/proc`, write its
own state, write the store trees it installs into), so it is given only that.

## 1. A dedicated user

```bash
sudo useradd --system --home-dir /var/lib/knight --shell /usr/sbin/nologin knight
sudo install -d -o knight -g knight -m 0750 /var/lib/knight
```

## 2. The systemd unit

```bash
sudo cp knight-agent.service /etc/systemd/system/knight-agent.service
# Edit ExecStart: --base-url, and one --store per Django store this agent manages;
# set ReadWritePaths to those store roots.
sudo systemctl daemon-reload
sudo systemctl enable --now knight-agent
```

The unit drops every capability (`CapabilityBoundingSet=`), forbids gaining any
(`NoNewPrivileges=true`), gives a read-only system with writes only to
`/var/lib/knight` and the store roots, and restricts sockets to HTTPS and syscalls
to `@system-service`. See the comments in the unit for the one place to loosen if a
Feature builds from source.

## 3. The AppArmor profile

```bash
sudo cp knight-agent.apparmor /etc/apparmor.d/usr.local.bin.knight-agent
sudo apparmor_parser -r /etc/apparmor.d/usr.local.bin.knight-agent
sudo aa-status | grep knight-agent   # confirm it is enforced
```

## Verification status

Authored and reviewed, not yet enforced against a live agent from this repository
(there is no Linux host in CI). Validate on the target before relying on the
confinement — a `permissive` load (`aa-complain`) first will log what the agent
actually touches so the profile can be tightened to it — the same "run it for real
before ticking it done" bar the rest of the deployment holds.
