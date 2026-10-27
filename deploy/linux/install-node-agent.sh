#!/usr/bin/env bash
# Reproducible Linux install for the Chorus NodeAgent (M0 install config).
# Creates the restricted service account, state directory and systemd unit;
# publishes the agent from this repository. Secrets live only in the
# environment file created by the operator; this script never writes or
# echoes them. Must run as root on the target node.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SERVICE_USER="${CHORUS_SERVICE_USER:-chorus-agent}"
SERVICE_GROUP="$SERVICE_USER"
STATE_DIR="${CHORUS_STATE_DIR:-/var/lib/chorus/agent}"
INSTALL_ROOT="${CHORUS_INSTALL_ROOT:-/opt/chorus/node-agent}"
ENV_FILE="${CHORUS_ENV_FILE:-/etc/chorus/node-agent.env}"
ENV_TEMPLATE="${CHORUS_ENV_TEMPLATE:-}" # operator-provided; copied with 0640 root:group

fail() { echo "[FAIL] $1" >&2; exit 1; }
ok() { echo "[OK] $1"; }

[[ $EUID -eq 0 ]] || fail "run as root on the target node"

getent group "$SERVICE_GROUP" >/dev/null || groupadd --system "$SERVICE_GROUP" || fail "could not create group $SERVICE_GROUP"
ok "group $SERVICE_GROUP present"

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --gid "$SERVICE_GROUP" --home-dir "$STATE_DIR" --no-create-home \
    --shell /usr/sbin/nologin "$SERVICE_USER" || fail "could not create user $SERVICE_USER"
fi
ok "user $SERVICE_USER present (no login shell)"

install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0700 "$STATE_DIR" || fail "could not create $STATE_DIR"
ok "state directory $STATE_DIR created with 0700 $SERVICE_USER:$SERVICE_GROUP"

echo "[..] publishing Chorus.NodeAgent (linux-x64, framework-dependent)"
dotnet publish "$REPO_ROOT/src/Chorus.NodeAgent/Chorus.NodeAgent.csproj" \
  -c Release -r linux-x64 --self-contained false \
  -o "$INSTALL_ROOT" --nologo -v quiet || fail "dotnet publish failed"
chown -R root:"$SERVICE_GROUP" "$INSTALL_ROOT"
chmod -R u=rwX,g=rX,o= "$INSTALL_ROOT"
ok "agent published to $INSTALL_ROOT (readable by $SERVICE_GROUP only)"

DOTNET_PATH="$(command -v dotnet)" || fail "dotnet is not on PATH"
install -d -m 0755 /etc/systemd/system
sed "s|@DOTNET@|$DOTNET_PATH|" "$REPO_ROOT/deploy/linux/chorus-node-agent.service" > /etc/systemd/system/chorus-node-agent.service \
  || fail "could not install systemd unit"
chmod 0644 /etc/systemd/system/chorus-node-agent.service
ok "systemd unit installed (ExecStart dotnet: $DOTNET_PATH)"

if [[ -n $ENV_TEMPLATE ]]; then
  install -d -m 0750 /etc/chorus
  install -o root -g "$SERVICE_GROUP" -m 0640 "$ENV_TEMPLATE" "$ENV_FILE" || fail "could not install env file"
  ok "env file installed at $ENV_FILE (0640 root:$SERVICE_GROUP, contents not displayed)"
else
  echo "[SKIP] no CHORUS_ENV_TEMPLATE given; create $ENV_FILE (0640 root:$SERVICE_GROUP) before starting"
fi

systemd-analyze verify chorus-node-agent.service || fail "systemd unit verification failed"
systemctl daemon-reload || fail "daemon-reload failed"
ok "unit verified and daemon reloaded; start with: systemctl enable --now chorus-node-agent"
