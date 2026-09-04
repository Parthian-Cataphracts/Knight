variable "hostname" {
  type    = string
  default = "knight-1"
}

variable "region" {
  type    = string
  default = "fra1"
}

variable "size" {
  type        = string
  description = "Droplet size. KNIGHT is comfortable on a small VM; the load test showed headroom."
  default     = "s-2vcpu-4gb"
}

variable "ssh_key_fingerprints" {
  type        = list(string)
  description = "Fingerprints of the SSH keys allowed in, so Ansible can reach the host."
}
