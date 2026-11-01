#!/usr/bin/env bash
# GameServerControl — Linux installer / updater for self-contained release tarballs.
#
# Run as root (or via sudo):
#   curl -fsSL https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/linux/install-from-release.sh | sudo bash
#
# Override env vars:
#   GSC_REPO       — owner/name (default: Nereziel/GameServerControl)
#   GSC_INSTALL    — install dir (default: /opt/gameservercontrol/agent)
#   GSC_USER       — service user (default: gsc)
#   GSC_VERSION    — pin a tag like v1.0.8 (default: latest)

set -euo pipefail

REPO="${GSC_REPO:-Nereziel/GameServerControl}"
INSTALL_DIR="${GSC_INSTALL:-/opt/gameservercontrol/agent}"
SERVICE_USER="${GSC_USER:-gsc}"
VERSION="${GSC_VERSION:-}"
UNIT_NAME="gameservercontrol-agent.service"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; GRAY='\033[0;90m'; NC='\033[0m'
step()  { echo -e "\n${CYAN}==>${NC} $*"; }
ok()    { echo -e "    ${GREEN}OK:${NC} $*"; }
skip()  { echo -e "    ${GRAY}-- $*${NC}"; }
warn()  { echo -e "    ${YELLOW}!! $*${NC}"; }
fail()  { echo -e "\n${RED}ERROR:${NC} $*\n"; exit 1; }

echo "GameServerControl installer (Linux)"
echo "-----------------------------------"
echo "Repository: $REPO"

[[ $EUID -eq 0 ]] || fail "Run as root (sudo)."

step "Checking prerequisites"
for tool in curl tar systemctl; do
    command -v "$tool" >/dev/null 2>&1 || fail "$tool not found in PATH."
done
ok "curl, tar, systemctl all present."
ok "Self-contained release — system .NET runtime is not required."

step "Resolving release from GitHub"
if [[ -n "$VERSION" ]]; then
    api_url="https://api.github.com/repos/$REPO/releases/tags/$VERSION"
else
    api_url="https://api.github.com/repos/$REPO/releases/latest"
fi
release_json="$(curl -fsSL -H "User-Agent: gsc-installer" "$api_url")"
tag="$(echo "$release_json" | grep -oP '"tag_name":\s*"\K[^"]+' | head -1)"
[[ -n "$tag" ]] || fail "Could not resolve release tag."
ok "Tag: $tag"

tar_url="$(echo "$release_json" | grep -oP '"browser_download_url":\s*"\Khttps://[^"]+GameServerControl-Agent-linux\.tar\.gz' | head -1)"
[[ -n "$tar_url" ]] || fail "Release $tag is missing GameServerControl-Agent-linux.tar.gz."

step "Downloading agent tarball"
tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT
curl -fsSL "$tar_url" -o "$tmp_dir/agent.tar.gz"
ok "Downloaded."

if systemctl is-active --quiet "$UNIT_NAME"; then
    step "Stopping existing $UNIT_NAME"
    systemctl stop "$UNIT_NAME"
    ok "Stopped."
fi

step "Installing into $INSTALL_DIR"
preserve_dir="$tmp_dir/preserved"
mkdir -p "$preserve_dir"
for f in appsettings.json servers.json tokens.json public-relay.json; do
    if [[ -f "$INSTALL_DIR/$f" ]]; then
        cp "$INSTALL_DIR/$f" "$preserve_dir/$f"
        skip "Preserving $f"
    fi
done

mkdir -p "$INSTALL_DIR"
find "$INSTALL_DIR" -mindepth 1 -delete
tar -xzf "$tmp_dir/agent.tar.gz" -C "$INSTALL_DIR"

for f in appsettings.json servers.json tokens.json public-relay.json; do
    if [[ -f "$preserve_dir/$f" ]]; then
        cp "$preserve_dir/$f" "$INSTALL_DIR/$f"
    fi
done

if [[ ! -f "$INSTALL_DIR/appsettings.json" ]] && [[ -f "$INSTALL_DIR/appsettings.json.example" ]]; then
    cp "$INSTALL_DIR/appsettings.json.example" "$INSTALL_DIR/appsettings.json"
    ok "Wrote default appsettings.json."
fi
[[ -f "$INSTALL_DIR/appsettings.json" ]] || fail "Release is missing appsettings.json.example."
[[ -f "$INSTALL_DIR/servers.json" ]] || echo '{"Servers":[]}' > "$INSTALL_DIR/servers.json"
[[ -f "$INSTALL_DIR/GameServerControl.Agent" ]] || fail "Self-contained agent executable is missing."
chmod +x "$INSTALL_DIR/GameServerControl.Agent"
ok "Agent unpacked."

step "SteamCMD"
steamcmd_dir="$INSTALL_DIR/SteamCMD"
steamcmd_sh="$steamcmd_dir/steamcmd.sh"
if [[ ! -f "$steamcmd_sh" ]]; then
    mkdir -p "$steamcmd_dir"
    curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz -o "$tmp_dir/steamcmd.tar.gz"
    tar -xzf "$tmp_dir/steamcmd.tar.gz" -C "$steamcmd_dir"
fi
[[ -f "$steamcmd_sh" ]] || fail "SteamCMD extraction completed but steamcmd.sh is missing."
chmod +x "$steamcmd_sh"
ln -sf "$steamcmd_sh" /usr/local/bin/steamcmd
ok "SteamCMD: $steamcmd_sh"

step "Service user '$SERVICE_USER'"
if ! id "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --create-home --shell /usr/sbin/nologin "$SERVICE_USER"
    ok "Created."
else
    skip "Already exists."
fi
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"

step "Installing systemd unit"
unit_src="$INSTALL_DIR/$UNIT_NAME"
[[ -f "$unit_src" ]] || fail "Tarball missing $UNIT_NAME."
cp "$unit_src" "/etc/systemd/system/$UNIT_NAME"
systemctl daemon-reload
systemctl enable "$UNIT_NAME" >/dev/null 2>&1 || true
systemctl start "$UNIT_NAME"
sleep 2
if systemctl is-active --quiet "$UNIT_NAME"; then
    ok "$UNIT_NAME running."
else
    warn "Service did not become active. Check: journalctl -u $UNIT_NAME -n 50"
fi

step "Reading the persisted API token"
api_token="$(grep -oP '"ApiToken"\s*:\s*"\K[^"]+' "$INSTALL_DIR/appsettings.json" | head -1 || true)"
if [[ -n "$api_token" && "$api_token" != "REPLACE_WITH_32_CHARS+" ]]; then
    echo ""
    echo -e "${YELLOW}   ----- AGENT API TOKEN (keep this private) -----${NC}"
    echo "   $api_token"
    echo -e "${YELLOW}   -----------------------------------------------${NC}"
else
    warn "API token is still a placeholder. Check the service log and $INSTALL_DIR/appsettings.json."
fi

echo ""
echo "Installed $tag."
echo "  Service: systemctl status $UNIT_NAME"
echo "  Logs:    journalctl -u $UNIT_NAME -f"
echo "  Web UI:  http://<this-host>:5099/"
echo "  Config:  $INSTALL_DIR/appsettings.json"
echo ""
