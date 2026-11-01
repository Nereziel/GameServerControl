#!/usr/bin/env bash
# GameServerControl Relay — one-command installer / updater from GitHub Releases.
#
# Run on the public Linux VPS:
#   curl -fsSL https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/relay/install-from-release.sh | sudo bash
#
# Optional env vars:
#   GSC_REPO           owner/name (default: Nereziel/GameServerControl)
#   GSC_VERSION        pin a release tag, e.g. v1.1.0 (default: latest)
#   GSC_RELAY_INSTALL  install dir (default: /opt/gameservercontrol/relay)
#   GSC_RELAY_STATE    state dir (default: /var/lib/gameservercontrol-relay)
#   GSC_RELAY_USER     service user (default: gsc-relay)
#   GSC_RELAY_PORT     Tailscale-only API port (default: 5100)
#   RELAY_TOKEN        explicit API token; existing token is preserved when omitted
#   PUBLIC_INTERFACE   override auto-detected public interface
#   TAILSCALE_INTERFACE override tailscale0
#   TAILSCALE_IP       override auto-detected Tailscale IPv4

set -euo pipefail

REPO="${GSC_REPO:-Nereziel/GameServerControl}"
VERSION="${GSC_VERSION:-}"
INSTALL_DIR="${GSC_RELAY_INSTALL:-/opt/gameservercontrol/relay}"
STATE_DIR="${GSC_RELAY_STATE:-/var/lib/gameservercontrol-relay}"
SERVICE_USER="${GSC_RELAY_USER:-gsc-relay}"
API_PORT="${GSC_RELAY_PORT:-5100}"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
step() { echo -e "\n${CYAN}==>${NC} $*"; }
ok()   { echo -e "    ${GREEN}OK:${NC} $*"; }
warn() { echo -e "    ${YELLOW}!! $*${NC}"; }
fail() { echo -e "\n${RED}ERROR:${NC} $*\n"; exit 1; }

[[ $EUID -eq 0 ]] || fail "Run as root (sudo)."

echo "GameServerControl Relay installer"
echo "---------------------------------"
echo "Repository: $REPO"

step "Checking prerequisites"
for tool in curl tar systemctl tailscale iptables ip; do
  command -v "$tool" >/dev/null 2>&1 || fail "$tool not found in PATH."
done
ok "Required tools are present."

step "Resolving GitHub release"
if [[ -n "$VERSION" ]]; then
  api_url="https://api.github.com/repos/$REPO/releases/tags/$VERSION"
else
  api_url="https://api.github.com/repos/$REPO/releases/latest"
fi
release_json="$(curl -fsSL -H "User-Agent: gsc-relay-installer" "$api_url")"
tag="$(echo "$release_json" | grep -oP '"tag_name":\s*"\K[^"]+' | head -1)"
[[ -n "$tag" ]] || fail "Could not resolve release tag."
tar_url="$(echo "$release_json" | grep -oP '"browser_download_url":\s*"\Khttps://[^"]+GameServerControl-Relay-linux\.tar\.gz' | head -1)"
[[ -n "$tar_url" ]] || fail "Release $tag is missing GameServerControl-Relay-linux.tar.gz."
ok "Tag: $tag"

step "Downloading relay"
tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT
curl -fsSL "$tar_url" -o "$tmp_dir/relay.tar.gz"
mkdir -p "$tmp_dir/relay"
tar -xzf "$tmp_dir/relay.tar.gz" -C "$tmp_dir/relay"
[[ -x "$tmp_dir/relay/GameServerControl.Relay" ]] || fail "Relay executable is missing from the release asset."
[[ -f "$tmp_dir/relay/install.sh" ]] || fail "Relay install.sh is missing from the release asset."
[[ -f "$tmp_dir/relay/gameservercontrol-relay.service" ]] || fail "Relay systemd unit is missing from the release asset."
chmod +x "$tmp_dir/relay/install.sh"
ok "Downloaded and verified."

step "Installing relay"
SOURCE_DIR="$tmp_dir/relay" \
INSTALL_DIR="$INSTALL_DIR" \
STATE_DIR="$STATE_DIR" \
SERVICE_USER="$SERVICE_USER" \
API_PORT="$API_PORT" \
RELAY_TOKEN="${RELAY_TOKEN:-}" \
PUBLIC_INTERFACE="${PUBLIC_INTERFACE:-}" \
TAILSCALE_INTERFACE="${TAILSCALE_INTERFACE:-}" \
TAILSCALE_IP="${TAILSCALE_IP:-}" \
bash "$tmp_dir/relay/install.sh"

if systemctl is-active --quiet gameservercontrol-relay.service; then
  ok "gameservercontrol-relay.service is running."
else
  warn "Relay service is not active. Check: journalctl -u gameservercontrol-relay.service -n 100"
fi

echo
echo "Installed GameServerControl Relay $tag."
echo "Future updates use the same one-line command."
