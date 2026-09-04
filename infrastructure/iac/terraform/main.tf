# A reference for the machine half of provisioning, to adapt to the chosen host.
#
# Terraform makes the machine; the Ansible role next door configures it. This
# example uses DigitalOcean because it is small to read, not because it is the
# decision — the hosting platform is still the product owner's call (self-service
# plan §11). Swap the provider and the one resource for AWS/Hetzner/GCP/etc.; the
# output the Ansible inventory needs (a public IP) is the same everywhere.

terraform {
  required_version = ">= 1.5"
  required_providers {
    digitalocean = {
      source  = "digitalocean/digitalocean"
      version = "~> 2.0"
    }
  }
}

provider "digitalocean" {
  # DIGITALOCEAN_TOKEN in the environment; never a token in a committed file.
}

resource "digitalocean_droplet" "knight" {
  name     = var.hostname
  image    = "ubuntu-24-04-x64"
  region   = var.region
  size     = var.size
  ssh_keys = var.ssh_key_fingerprints

  # Only SSH in; the API is reached over TLS through nginx, which listens on the
  # public interface, while the app itself binds 127.0.0.1 (the Ansible role's job).
  tags = ["knight", "control-plane"]
}

output "knight_host" {
  description = "Public IP — put it in the Ansible inventory's [knight] group."
  value       = digitalocean_droplet.knight.ipv4_address
}

output "ansible_inventory_hint" {
  value = "echo '[knight]\\nknight-1 ansible_host=${digitalocean_droplet.knight.ipv4_address} ansible_user=root' > ../ansible/inventory.ini"
}
