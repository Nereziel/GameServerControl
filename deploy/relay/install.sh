#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="${INSTALL_DIR:-/opt/gameservercontrol/relay}"
STATE_DIR="${STATE_DIR:-/var/lib/gameservercontrol-relay}"
SERVICE_USER="${SERVICE_USER:-gsc-relay}"
UNIT_NAME="gameservercontrol-relay.service"
API_PORT="${API_PORT:-5100}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SOURCE_DIR="${SOURCE_DIR:-$SCRIPT_DIR}"

if [[ $EUID -ne 0 ]]; then
  echo "Run as root: sudo $0" >&2
  exit 1
fi
if ! command -v tailscale >/dev/null 2>&1; then
  echo "tailscale CLI is required on the VPS." >&2
  exit 1
fi
if ! command -v iptables >/dev/null 2>&1; then
  echo "iptables is required (Ubuntu's iptables-nft compatibility layer is supported)." >&2
  exit 1
fi
if [[ ! -x "$SOURCE_DIR/GameServerControl.Relay" ]]; then
  echo "GameServerControl.Relay binary not found in $SOURCE_DIR." >&2
  exit 1
fi

TAILSCALE_IP="${TAILSCALE_IP:-$(tailscale ip -4 | head -n1)}"
PUBLIC_INTERFACE="${PUBLIC_INTERFACE:-$(ip -4 route show default | awk '/default/ {print $5; exit}')}"
TAILSCALE_INTERFACE="${TAILSCALE_INTERFACE:-tailscale0}"
if [[ -z "$TAILSCALE_IP" || -z "$PUBLIC_INTERFACE" ]]; then
  echo "Could not detect Tailscale IPv4 address or public interface." >&2
  exit 1
fi

if [[ -z "${RELAY_TOKEN:-}" ]]; then
  if command -v openssl >/dev/null 2>&1; then
    RELAY_TOKEN="$(openssl rand -hex 32)"
  else
    RELAY_TOKEN="$(head -c 48 /dev/urandom | base64 | tr -d '\n')"
  fi
fi

if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --home "$STATE_DIR" --shell /usr/sbin/nologin "$SERVICE_USER"
fi

mkdir -p "$INSTALL_DIR" "$STATE_DIR"
find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
cp -a "$SOURCE_DIR"/. "$INSTALL_DIR"/
chmod +x "$INSTALL_DIR/GameServerControl.Relay"
chown -R root:root "$INSTALL_DIR"
chown -R "$SERVICE_USER:$SERVICE_USER" "$STATE_DIR"

cat >/etc/gameservercontrol-relay.env <<EOF
Relay__Bind=http://${TAILSCALE_IP}:${API_PORT}
Relay__ApiToken=${RELAY_TOKEN}
Relay__PublicInterface=${PUBLIC_INTERFACE}
Relay__TailscaleInterface=${TAILSCALE_INTERFACE}
Relay__StatePath=${STATE_DIR}/routes.json
Relay__IptablesPath=$(command -v iptables)
EOF
chmod 600 /etc/gameservercontrol-relay.env

cat >/etc/sysctl.d/99-gameservercontrol-relay.conf <<'EOF'
net.ipv4.ip_forward = 1
EOF
sysctl -p /etc/sysctl.d/99-gameservercontrol-relay.conf >/dev/null

cp "$SCRIPT_DIR/$UNIT_NAME" "/etc/systemd/system/$UNIT_NAME"
systemctl daemon-reload
systemctl enable "$UNIT_NAME"
systemctl restart "$UNIT_NAME"

echo
echo "GameServerControl Relay installed."
echo "  API:              http://${TAILSCALE_IP}:${API_PORT}"
echo "  Public interface: ${PUBLIC_INTERFACE}"
echo "  Tailscale iface:  ${TAILSCALE_INTERFACE}"
echo "  State:             ${STATE_DIR}/routes.json"
echo "  API token:         ${RELAY_TOKEN}"
echo
echo "Put the API URL + token into Agent:PublicRelay."
echo "The relay manages only its GSC_RELAY_* chains; it does not rewrite /etc/ufw/before.rules."
echo "Remember to allow the game ports in your VPS provider/cloud firewall as well."
