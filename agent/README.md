# The KNIGHT agent

A small daemon that runs on a managed server. It tells KNIGHT how the machine is
and carries out the feature-delivery jobs queued for the stores on it.

## What it is allowed to do

Three properties define it, and none is negotiable ([`risks.md` R22](../docs/risks.md)):

- **A closed vocabulary, never a command.** KNIGHT can ask it to apply a store's
  queued installation jobs. No field, endpoint or code path here takes a command,
  a path or a script. A compromised control plane must not become arbitrary code
  execution across every managed server at once.
- **It reaches out; nothing reaches in.** It opens connections to KNIGHT and
  listens on no port, so a managed server needs no inbound firewall rule.
- **Its credential is revocable and machine-bound.** Revoking an agent in KNIGHT
  takes effect on its next call, not when a token expires.

It has no third-party dependencies, by design.

## Installing

```bash
pip install ./agent
```

## Enrolling

Provision an agent in KNIGHT — **Servers → the server → Add agent** — which shows
a one-time provisioning token exactly once. Then, on the machine:

```bash
knight-agent --base-url https://knight.example.com \
             --state /var/lib/knight/agent.json \
             enrol --token <the provisioning token>
```

The token is burned on success. The credential it returns is written to the state
file with owner-only permissions and is the whole of the agent's authority — it is
the thing to protect on the box.

## Running

```bash
knight-agent --base-url https://knight.example.com \
             --store /srv/stores/cafe-parthia \
             --store /srv/stores/another-store \
             run
```

`--store` is repeatable and names a Django store directory this agent manages.
`run --once` does a single pass and exits, which is what to use from cron or a
systemd timer if you would rather not run a long-lived process.

### As a systemd unit

```ini
[Unit]
Description=KNIGHT agent
After=network-online.target

[Service]
Type=simple
User=knight
ExecStart=/usr/local/bin/knight-agent --base-url https://knight.example.com --store /srv/stores/cafe-parthia run
Restart=always
RestartSec=30

# The agent needs to read /proc and write its own state. Nothing else.
ProtectSystem=strict
ReadWritePaths=/var/lib/knight
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
```

## What it reports

CPU, memory, disk, network and load average, read from the standard library and
`/proc`. Anything it cannot measure on a given platform is omitted rather than
guessed — and never raised, because an agent that stops reporting because it could
not read a network counter has turned a cosmetic gap into an outage.

## Troubleshooting

`knight-agent: KNIGHT refused this agent's credential` means the agent was
revoked. That is a deliberate act by an operator; provision a new agent for the
server rather than retrying.

If the state file is unreadable the agent refuses to start rather than enrolling
again — re-enrolling would burn a second provisioning token and leave KNIGHT with
two records for one machine, only one of which anybody is watching.
