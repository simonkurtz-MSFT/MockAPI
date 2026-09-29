#!/usr/bin/env bash
set -euo pipefail

# Run only on disposable GitHub-hosted runners. Docker's classic image store
# converts OCI manifests during load/push; preserve the tested OCI image instead.
configuration="$(mktemp)"
trap 'rm -f "$configuration" "$configuration.updated"' EXIT
if [[ -f /etc/docker/daemon.json ]]; then
  sudo cat /etc/docker/daemon.json > "$configuration"
else
  printf '{}\n' > "$configuration"
fi
jq '.features["containerd-snapshotter"] = true' "$configuration" > "$configuration.updated"
sudo install -D -m 644 "$configuration.updated" /etc/docker/daemon.json
rm -f "$configuration.updated"
sudo systemctl restart docker
docker info --format '{{json .DriverStatus}}' | grep -q 'io.containerd.snapshotter.v1'
