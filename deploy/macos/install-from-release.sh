#!/usr/bin/env bash
# GameServerControl — macOS installer / updater that downloads pre-built release tarballs.
#
# Run with:
#   curl -fsSL https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/macos/install-from-release.sh | bash
#
# Override env vars:
#   GSC_REPO       — owner/name (default: Nereziel/GameServerControl)
#   GSC_INSTALL    — install dir (default: ~/Library/Application Support/GameServerControl/Agent)
#   GSC_VERSION    — pin a specific tag like v1.0.7 (default: latest)

set -euo pipefail

REPO="${GSC_REPO:-Nereziel/GameServerControl}"
DEFAULT_INSTALL="$HOME/Library/Application Support/GameServerControl/Agent"
INSTALL_DIR="${GSC_INSTALL:-$DEFAULT_INSTALL}"
LOG_DIR="$HOME/Library/Logs/GameServerControl"
LAUNCH_AGENT_DIR="$HOME/Library/LaunchAgents"
PLIST_NAME="com.tk-eclipse.gameservercontrol-agent.plist"
PLIST_DST="$LAUNCH_AGENT_DIR/$PLIST_NAME"
VERSION="${GSC_VERSION:-}"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; GRAY='\033[0;90m'; NC='\033[0m'
step()  { echo -e "\n${CYAN}==>${NC} $*"; }
ok()    { echo -e "    ${GREEN}OK:${NC} $*"; }
skip()  { echo -e "    ${GRAY}-- $*${NC}"; }
warn()  { echo -e "    ${YELLOW}!! $*${NC}"; }
fail()  { echo -e "\n${RED}ERROR:${NC} $*\n"; exit 1; }

echo "GameServerControl installer (macOS)"
echo "-----------------------------------"
echo "Repository: $REPO"

arch_raw="$(uname -m)"
case "$arch_raw" in
    arm64)  arch="arm64"  ;;
    x86_64) arch="x64"    ;;
    *)      fail "Unsupported architecture: $arch_raw (expected arm64 or x86_64)." ;;
esac
ok "Architecture: $arch"

step "Checking prerequisites"
for tool in curl tar launchctl; do
    command -v "$tool" >/dev/null 2>&1 || fail "$tool not found in PATH (this is unusual on macOS)."
done
ok "curl, tar, launchctl all present."

step ".NET 8 ASP.NET runtime"
DOTNET_ROOT_DEFAULT="$HOME/.dotnet"
DOTNET_ROOT_SYS="/usr/local/share/dotnet"
DOTNET_ROOT=""
if command -v dotnet >/dev/null 2>&1 && dotnet --list-runtimes 2>/dev/null | grep -q 'Microsoft.AspNetCore.App 8\.'; then
    DOTNET_ROOT="$(dirname "$(command -v dotnet)")"
    if [[ -d "$DOTNET_ROOT_SYS" ]]; then DOTNET_ROOT="$DOTNET_ROOT_SYS"; fi
    if [[ -d "$DOTNET_ROOT_DEFAULT" ]]; then DOTNET_ROOT="$DOTNET_ROOT_DEFAULT"; fi
    ok "Already present at $DOTNET_ROOT."
else
    warn "Missing — installing via Microsoft's dotnet-install.sh script (user-scoped, no sudo)."
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --runtime aspnetcore --channel 8.0 --install-dir "$DOTNET_ROOT_DEFAULT"
    DOTNET_ROOT="$DOTNET_ROOT_DEFAULT"
    if ! grep -q 'DOTNET_ROOT="$HOME/.dotnet"' "$HOME/.zshrc" 2>/dev/null; then
        {
            echo ''
            echo '# GameServerControl: .NET 8 runtime'
            echo 'export DOTNET_ROOT="$HOME/.dotnet"'
            echo 'export PATH="$DOTNET_ROOT:$PATH"'
        } >> "$HOME/.zshrc"
        ok "Added DOTNET_ROOT to ~/.zshrc."
    fi
    ok ".NET 8 runtime installed at $DOTNET_ROOT."
fi

step "Resolving release from GitHub"
if [[ -n "$VERSION" ]]; then
    api_url="https://api.github.com/repos/$REPO/releases/tags/$VERSION"
else
    api_url="https://api.github.com/repos/$REPO/releases/latest"
fi
release_json="$(curl -fsSL -H "User-Agent: gsc-installer" "$api_url")"
tag="$(echo "$release_json" | grep -oE '"tag_name":[[:space:]]*"[^"]+"' | head -1 | sed -E 's/.*"([^"]+)"$/\1/')"
[[ -n "$tag" ]] || fail "Could not resolve release tag."
ok "Tag: $tag"

asset_name="GameServerControl-Agent-macos-${arch}.tar.gz"
tar_url="$(echo "$release_json" | grep -oE '"browser_download_url":[[:space:]]*"https://[^"]+'"$asset_name"'"' | head -1 | sed -E 's/.*"(https[^"]+)"$/\1/')"
[[ -n "$tar_url" ]] || fail "Release $tag is missing $asset_name."

step "Downloading $asset_name"
tmp_dir="$(mktemp -d -t gsc-install)"
trap "rm -rf $tmp_dir" EXIT
curl -fsSL "$tar_url" -o "$tmp_dir/agent.tar.gz"
ok "Downloaded ($(du -h "$tmp_dir/agent.tar.gz" | cut -f1))"

if launchctl list | grep -q 'com.tk-eclipse.gameservercontrol-agent'; then
    step "Unloading existing LaunchAgent"
    launchctl unload "$PLIST_DST" 2>/dev/null || true
    sleep 1
    ok "Stopped."
fi

step "Installing into $INSTALL_DIR"
preserve_dir="$tmp_dir/preserved"
mkdir -p "$preserve_dir"
for f in appsettings.json servers.json tokens.json; do
    if [[ -f "$INSTALL_DIR/$f" ]]; then
        cp "$INSTALL_DIR/$f" "$preserve_dir/$f"
        skip "Preserving $f"
    fi
done

mkdir -p "$INSTALL_DIR" "$LOG_DIR" "$LAUNCH_AGENT_DIR"
find "$INSTALL_DIR" -mindepth 1 -delete
tar -xzf "$tmp_dir/agent.tar.gz" -C "$INSTALL_DIR"

for f in appsettings.json servers.json tokens.json; do
    if [[ -f "$preserve_dir/$f" ]]; then
        cp "$preserve_dir/$f" "$INSTALL_DIR/$f"
    fi
done

if [[ ! -f "$INSTALL_DIR/appsettings.json" ]] && [[ -f "$INSTALL_DIR/appsettings.json.example" ]]; then
    cp "$INSTALL_DIR/appsettings.json.example" "$INSTALL_DIR/appsettings.json"
    ok "Wrote default appsettings.json (token generates on first boot)."
fi
[[ -f "$INSTALL_DIR/appsettings.json" ]] || fail "Release is missing appsettings.json.example."
[[ -f "$INSTALL_DIR/servers.json" ]] || echo '{"Servers":[]}' > "$INSTALL_DIR/servers.json"

chmod +x "$INSTALL_DIR/GameServerControl.Agent" 2>/dev/null || true
ok "Unpacked."

step "Installing LaunchAgent plist"
plist_template="$INSTALL_DIR/$PLIST_NAME"
[[ -f "$plist_template" ]] || fail "Tarball missing $PLIST_NAME — bad release artifact."

sed -e "s|{{INSTALL_DIR}}|$INSTALL_DIR|g" \
    -e "s|{{LOG_DIR}}|$LOG_DIR|g" \
    -e "s|{{DOTNET_ROOT}}|$DOTNET_ROOT|g" \
    "$plist_template" > "$PLIST_DST"

launchctl load -w "$PLIST_DST"
sleep 3
if launchctl list | grep -q 'com.tk-eclipse.gameservercontrol-agent'; then
    ok "LaunchAgent loaded + running."
else
    warn "LaunchAgent did not start. Check $LOG_DIR/agent.err.log"
fi

step "Reading the persisted API token"
api_token="$(grep -Eo '"ApiToken"[[:space:]]*:[[:space:]]*"[^"]+' "$INSTALL_DIR/appsettings.json" | head -1 | sed -E 's/.*"([^"]+)$/\1/' || true)"
if [[ -n "$api_token" && "$api_token" != "REPLACE_WITH_32_CHARS+" ]]; then
    echo ""
    echo -e "${YELLOW}   ----- AGENT API TOKEN (keep this private) -----${NC}"
    echo "   $api_token"
    echo -e "${YELLOW}   -----------------------------------------------${NC}"
else
    warn "API token is still a placeholder. Check $LOG_DIR/agent.err.log and $INSTALL_DIR/appsettings.json."
fi

echo ""
echo "Installed $tag."
echo ""
echo "  Status:  launchctl list | grep gameservercontrol"
echo "  Logs:    tail -f $LOG_DIR/agent.out.log"
echo "  Errors:  tail -f $LOG_DIR/agent.err.log"
echo "  Web UI:  http://<this-mac>:5099/"
echo "  Config:  $INSTALL_DIR/appsettings.json"
echo "  Unload:  launchctl unload $PLIST_DST"
echo ""
