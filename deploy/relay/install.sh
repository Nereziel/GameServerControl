#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="${INSTALL_DIR:-/opt/gameservercontrol/relay}"
STATE_DIR="${STATE_DIR:-/var/lib/gameservercontrol-relay}"
SERVICE_USER="${SERVICE_USER:-gsc-relay}"
UNIT_NAME="gameservercontrol-relay.service"
ENV_FILE="/etc/gameservercontrol-relay.env"
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
if [[ ! -f "$SOURCE_DIR/$UNIT_NAME" ]]; then
  echo "$UNIT_NAME not found in $SOURCE_DIR." >&2
  exit 1
fi

has_icu() {
  if command -v ldconfig >/dev/null 2>&1 && ldconfig -p 2>/dev/null | grep -qE 'libicu(uc|i18n)\.so'; then
    return 0
  fi
  find /usr/lib /lib -type f -name 'libicuuc.so*' -print -quit 2>/dev/null | grep -q .
}

if ! has_icu; then
  echo "ICU runtime not found; installing the .NET globalization dependency."
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update
    DEBIAN_FRONTEND=noninteractive apt-get install -y libicu-dev
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y libicu
  elif command -v yum >/dev/null 2>&1; then
    yum install -y libicu
  elif command -v apk >/dev/null 2>&1; then
    apk add --no-cache icu-libs
  else
    echo "ICU is required by the self-contained .NET Relay. Install libicu (or icu-libs) with your package manager and rerun this installer." >&2
    exit 1
  fi
  if ! has_icu; then
    echo "ICU installation completed but libicu could not be detected. Install libicu manually and rerun this installer." >&2
    exit 1
  fi
fi

TAILSCALE_IP="${TAILSCALE_IP:-$(tailscale ip -4 | head -n1)}"
PUBLIC_INTERFACE="${PUBLIC_INTERFACE:-$(ip -4 route show default | awk '/default/ {print $5; exit}')}"
TAILSCALE_INTERFACE="${TAILSCALE_INTERFACE:-tailscale0}"
if [[ -z "$TAILSCALE_IP" || -z "$PUBLIC_INTERFACE" ]]; then
  echo "Could not detect Tailscale IPv4 address or public interface." >&2
  exit 1
fi

# Updates must not silently rotate the Relay API token because the Agent stores it.
if [[ -z "${RELAY_TOKEN:-}" && -f "$ENV_FILE" ]]; then
  RELAY_TOKEN="$(sed -n 's/^Relay__ApiToken=//p' "$ENV_FILE" | head -n1)"
fi
if [[ -z "${RELAY_TOKEN:-}" ]]; then
  if command -v openssl >/dev/null 2>&1; then
    RELAY_TOKEN="$(openssl rand -hex 32)"
  else
    RELAY_TOKEN="$(head -c 48 /dev/urandom | base64 | tr -d '\n')"
  fi
fi
if [[ ${#RELAY_TOKEN} -lt 24 ]]; then
  echo "Relay API token must contain at least 24 characters." >&2
  exit 1
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

cat >"$ENV_FILE" <<EOF
Relay__Bind=http://${TAILSCALE_IP}:${API_PORT}
Relay__ApiToken=${RELAY_TOKEN}
Relay__PublicInterface=${PUBLIC_INTERFACE}
Relay__TailscaleInterface=${TAILSCALE_INTERFACE}
Relay__StatePath=${STATE_DIR}/routes.json
Relay__IptablesPath=$(command -v iptables)
EOF
chmod 600 "$ENV_FILE"

cat >/etc/sysctl.d/99-gameservercontrol-relay.conf <<'EOF'
net.ipv4.ip_forward = 1
EOF
sysctl -p /etc/sysctl.d/99-gameservercontrol-relay.conf >/dev/null

# The Relay API is intentionally reachable only over Tailscale. If UFW is active,
# explicitly allow the API on the Tailscale interface; no public-interface API rule is added.
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q '^Status: active'; then
  ufw allow in on "$TAILSCALE_INTERFACE" to "$TAILSCALE_IP" port "$API_PORT" proto tcp comment 'GameServerControl Relay API' >/dev/null
fi

cp "$SOURCE_DIR/$UNIT_NAME" "/etc/systemd/system/$UNIT_NAME"
systemctl daemon-reload
systemctl enable "$UNIT_NAME" >/dev/null 2>&1 || true
systemctl restart "$UNIT_NAME"
sleep 2

if ! systemctl is-active --quiet "$UNIT_NAME"; then
  echo "Relay service failed to start." >&2
  journalctl -u "$UNIT_NAME" -n 50 --no-pager >&2 || true
  exit 1
fi

# Test the exact address the Agent will use. This catches bind failures, UFW rules,
# invalid environment configuration and early service crashes during installation.
if command -v curl >/dev/null 2>&1; then
  if ! curl -fsS --connect-timeout 5 -H "Authorization: Bearer ${RELAY_TOKEN}" \
      "http://${TAILSCALE_IP}:${API_PORT}/api/health" >/dev/null; then
    echo "Relay service is running, but its API health check failed on http://${TAILSCALE_IP}:${API_PORT}." >&2
    echo "Listener check: ss -lntp | grep ':${API_PORT}'" >&2
    echo "Service logs:  journalctl -u ${UNIT_NAME} -n 100 --no-pager" >&2
    journalctl -u "$UNIT_NAME" -n 50 --no-pager >&2 || true
    exit 1
  fi
fi

echo
echo "GameServerControl Relay installed."
echo "  API:              http://${TAILSCALE_IP}:${API_PORT}"
echo "  Public interface: ${PUBLIC_INTERFACE}"
echo "  Tailscale iface:  ${TAILSCALE_INTERFACE}"
echo "  State:             ${STATE_DIR}/routes.json"
echo "  API token:         ${RELAY_TOKEN}"
echo
echo "Configure the Relay URL + token in the Agent Web UI under Server -> Network -> Public VPS relay."
echo "The relay manages only its GSC_RELAY_* chains; it does not rewrite /etc/ufw/before.rules."
echo "If UFW is active, TCP ${API_PORT} is allowed only on ${TAILSCALE_INTERFACE}."
echo "Remember to allow the public game ports in your VPS provider/cloud firewall as well."
