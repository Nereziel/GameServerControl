#!/usr/bin/env bash
# GameServerControl Agent — Linux installer.
#
# Builds + installs the agent under /opt/gameservercontrol/agent and registers
# a systemd unit. Designed to run on Debian/Ubuntu and Fedora/RHEL-likes.
# Requires sudo for systemd integration + user creation.

set -euo pipefail
umask 077

INSTALL_DIR="${INSTALL_DIR:-/opt/gameservercontrol/agent}"
SERVICE_USER="${SERVICE_USER:-gsc}"
GAME_ROOT="${GAME_ROOT:-/srv/gameservers}"
BACKUP_ROOT="${BACKUP_ROOT:-/var/lib/gameservercontrol/backups}"
UNIT_NAME="gameservercontrol-agent.service"
REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
STATE_TMP="$(mktemp -d)"
trap 'rm -rf "$STATE_TMP"' EXIT

echo "=== GameServerControl Agent — Linux install ==="
echo "Install dir : $INSTALL_DIR"
echo "Service user: $SERVICE_USER"
echo "Game root   : $GAME_ROOT"
echo "Backup root : $BACKUP_ROOT"
echo

if [[ $EUID -ne 0 ]]; then
  echo "This script needs root (it creates a system user + installs a systemd unit)."
  echo "Re-run with: sudo $0"
  exit 1
fi

# 1) .NET 8 SDK present?
if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: dotnet CLI not found. Install .NET 8 SDK first:"
  echo "  Debian/Ubuntu: https://learn.microsoft.com/dotnet/core/install/linux-ubuntu"
  echo "  Fedora/RHEL:   https://learn.microsoft.com/dotnet/core/install/linux-rhel"
  exit 1
fi

# 2) Build + publish
echo "=== Building agent ==="
cd "$REPO_ROOT"
dotnet publish src/GameServerControl.Agent -c Release -r linux-x64 --self-contained false -o "$INSTALL_DIR.tmp"

# 3) Create service user if missing
if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  echo "=== Creating system user $SERVICE_USER ==="
  useradd --system --create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

# 4) Stop existing service if running (we're about to overwrite the binaries)
if systemctl is-active --quiet "$UNIT_NAME"; then
  echo "=== Stopping existing $UNIT_NAME ==="
  systemctl stop "$UNIT_NAME"
fi

# Preserve restricted Agent state before replacing the build payload. The Linux encryption
# master key must travel with its ciphertext or existing encrypted state becomes unrecoverable.
if [[ -d "$INSTALL_DIR" ]]; then
  chmod 700 "$INSTALL_DIR"
  for f in appsettings.json servers.json tokens.json public-relay.json secret-protection.key; do
    if [[ -f "$INSTALL_DIR/$f" ]]; then
      chmod 600 "$INSTALL_DIR/$f"
      cp "$INSTALL_DIR/$f" "$STATE_TMP/$f"
    fi
  done
fi

# 5) Move into place
echo "=== Installing into $INSTALL_DIR ==="
mkdir -p "$(dirname "$INSTALL_DIR")"
rm -rf "$INSTALL_DIR"
mv "$INSTALL_DIR.tmp" "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/GameServerControl.Agent" 2>/dev/null || true

for f in appsettings.json servers.json tokens.json public-relay.json secret-protection.key; do
  [[ -f "$STATE_TMP/$f" ]] && cp "$STATE_TMP/$f" "$INSTALL_DIR/$f"
done

# Managed roots match GameProfile.DefaultInstallPath + the systemd BackupRoot override.
mkdir -p "$GAME_ROOT" "$BACKUP_ROOT"
chown -R "$SERVICE_USER:$SERVICE_USER" "$GAME_ROOT" "$(dirname "$BACKUP_ROOT")"

# 6) Default config if absent
if [[ ! -f "$INSTALL_DIR/appsettings.json" ]]; then
  cp "$INSTALL_DIR/appsettings.json.example" "$INSTALL_DIR/appsettings.json"
  echo "Wrote default appsettings.json (agent will generate API token on first boot)."
fi
if [[ ! -f "$INSTALL_DIR/servers.json" ]]; then
  echo '{"Servers":[]}' > "$INSTALL_DIR/servers.json"
fi

chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"
chmod 700 "$INSTALL_DIR"
for f in appsettings.json servers.json tokens.json public-relay.json secret-protection.key; do
  [[ -f "$INSTALL_DIR/$f" ]] && chmod 600 "$INSTALL_DIR/$f"
done

# 7) Install systemd unit. The shipped unit grants write access to /srv/gameservers and the
# default backup root. Custom GAME_ROOT/BACKUP_ROOT values require a matching systemd override.
echo "=== Installing systemd unit ==="
cp "$REPO_ROOT/deploy/linux/$UNIT_NAME" "/etc/systemd/system/$UNIT_NAME"
if [[ "$GAME_ROOT" != "/srv/gameservers" || "$BACKUP_ROOT" != "/var/lib/gameservercontrol/backups" ]]; then
  echo "WARNING: custom GAME_ROOT/BACKUP_ROOT selected. Add them to ReadWritePaths and set Agent__BackupRoot in a systemd override."
fi
systemctl daemon-reload
systemctl enable "$UNIT_NAME"
systemctl start "$UNIT_NAME"

# 8) Do not scrape or print the generated credential from journald/stdout.
sleep 2
echo
echo "=== Secret state ==="
echo "API credentials and the Linux secret-protection key remain in restricted local state files and are not printed to installer/service stdout."

echo
echo "=== Done ==="
echo "Service status: systemctl status $UNIT_NAME"
echo "Logs:           journalctl -u $UNIT_NAME -f"
echo "Web UI:         http://<this-host>:5099/"
echo "Games:          $GAME_ROOT"
echo "Backups:        $BACKUP_ROOT"
echo "Edit config:    $INSTALL_DIR/appsettings.json"
