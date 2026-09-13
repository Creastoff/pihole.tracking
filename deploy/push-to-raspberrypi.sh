#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
    cat <<'EOF'
Usage: push-to-raspberrypi.sh PI_HOST [PI_USER] [PI_PORT] [REMOTE_DIRECTORY]

Examples:
  bash ./deploy/push-to-raspberrypi.sh raspberry-pi-hostname pi
  bash ./deploy/push-to-raspberrypi.sh 192.168.1.20 kristof 2222 pihole-domain-review

The Raspberry Pi must have SSH access and Docker Compose installed. The SSH
user must be able to run Docker without an interactive sudo password prompt.
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" || "$#" -lt 1 || "$#" -gt 4 ]]; then
    usage
    [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]] && exit 0
    exit 2
fi

pi_host="$1"
pi_user="${2:-pi}"
pi_port="${3:-22}"
remote_directory="${4:-pihole-domain-review}"

die() {
    printf 'Error: %s\n' "$1" >&2
    exit 1
}

command -v ssh >/dev/null 2>&1 || die 'ssh is required.'
command -v tar >/dev/null 2>&1 || die 'tar is required.'

[[ "$pi_port" =~ ^[0-9]+$ && "$pi_port" -ge 1 && "$pi_port" -le 65535 ]] \
    || die 'PI_PORT must be between 1 and 65535.'
[[ "$pi_user" =~ ^[a-z_][a-z0-9_-]*$ ]] \
    || die 'PI_USER may contain lowercase letters, digits, underscores, and hyphens.'
[[ "$remote_directory" =~ ^[A-Za-z0-9._/-]+$ && "$remote_directory" != /* && "$remote_directory" != *..* ]] \
    || die 'REMOTE_DIRECTORY must be a relative path without .. or shell-special characters.'

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_directory/.." && pwd)"
[[ -f "$repo_root/compose.yaml" && -f "$repo_root/Dockerfile" \
    && -f "$repo_root/deploy/deploy.exclude" \
    && -f "$repo_root/deploy/start-container.sh" ]] \
    || die "Run this script from the repository's deploy directory."

remote_path="\$HOME/$remote_directory"
ssh_target="$pi_user@$pi_host"

printf 'Deploying %s to %s:%s...\n' "$repo_root" "$ssh_target" "$remote_path"
printf 'OpenSSH will ask for the SSH password or key passphrase if needed.\n'

tar -czf - \
    --exclude-from="$repo_root/deploy/deploy.exclude" \
    -C "$repo_root" . \
| ssh -p "$pi_port" -o StrictHostKeyChecking=accept-new "$ssh_target" \
    "set -eu;
     mkdir -p \"$remote_path\";
     tar -xzf - -C \"$remote_path\";
     cd \"$remote_path\";
     bash deploy/start-container.sh"

printf 'Deployment complete. Open http://%s:5178/\n' "$pi_host"
