# Infrastructure as code

The declarative replacement for the `install.sh` / `knightctl.sh` shell installers
(hardening backlog P1). The Bash installers are imperative, not idempotent, and
hard to debug when a package manager or the network hiccups half-way; this does
the same work as code that converges on re-run.

```
iac/
├── terraform/   the machine — a provider reference to adapt (DigitalOcean shown)
└── ansible/     the configuration — provider-agnostic, over SSH to any Ubuntu/Debian host
```

## Two halves, one seam

- **Terraform makes the machine.** `terraform/` is a small reference; the hosting
  platform is still the product owner's decision (self-service plan §11), so it is
  written to be swapped — the provider and the one droplet resource change, the
  public-IP output the inventory needs does not. This is also the layer where a
  container platform (Kubernetes/Nomad) and the per-tenant **immutable-image**
  delivery option from [`adr/0036`](../../docs/adr/0036-feature-delivery-runtime-install-versus-immutable-images.md)
  would live.
- **Ansible configures it.** `ansible/roles/knight` is the real, provider-agnostic
  replacement for `install.sh`'s logic — packages, the .NET toolchain into the
  install's own directory, the PostgreSQL role and database, Redis, checkout,
  publish, migrations, the systemd unit (hardened: `NoNewPrivileges`,
  `ProtectSystem=strict`), the first administrator via `Knight.Bootstrap`, the
  single-hostname nginx site (127.0.0.1 upstream) with certbot TLS, and the
  nightly backup. Every step is idempotent.

## Run it

```bash
# 1. The machine (adapt terraform/ to your provider first).
cd terraform && terraform init && terraform apply

# 2. The configuration. Secrets from a vault, never a committed file.
cd ../ansible
cp inventory.example.ini inventory.ini      # fill in the host
ansible-vault create group_vars/knight/vault.yml   # db password, jwt key, admin password
ansible-playbook -i inventory.ini site.yml --ask-vault-pass
```

## Verification status

The Ansible YAML is syntax-checked; the Terraform is authored against the
DigitalOcean provider's current schema. **Neither has yet been run against a live
host from this repository** — there is no Ansible/Terraform in CI, and the hosting
platform is unchosen. Per the project's end-of-phase rule, this is not ticked
"done" until it has provisioned a real server end to end, the same bar the Bash
installer cleared across real machines in phase 11
([`docs/phase-11-verification.md`](../../docs/phase-11-verification.md)). Until
then `install.sh` remains the verified path and this is its successor-in-waiting.
