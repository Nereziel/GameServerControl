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
umask 077

REPO="${GSC_REPO:-Nereziel/GameServerControl}"
INSTALL_DIR="${GSC_INSTALL:-/opt/gameservercontrol/agent}"
PREVIOUS_DIR="${INSTALL_DIR}.previous"
SERVICE_USER="${GSC_USER:-gsc}"
VERSION="${GSC_VERSION:-}"
UNIT_NAME="gameservercontrol-agent.service"
HAD_PREVIOUS=0
UPDATE_HEALTH_TOKEN_FILE="$INSTALL_DIR/.update-health-token"
UPDATE_HEALTH_TOKEN=""

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
for tool in curl tar systemctl grep awk od tr hostname; do
    command -v "$tool" >/dev/null 2>&1 || fail "$tool not found in PATH."
done
ok "curl, tar, systemctl all present."
ok "Self-contained release — system .NET runtime is not required."

local_health_url() {
    local bind="${1:-http://127.0.0.1:5099}"
    if [[ "$bind" =~ ^(https?)://(\[[^]]+\]|[^/:]+)(:([0-9]+))?/?$ ]]; then
        local scheme="${BASH_REMATCH[1]}"
        local host="${BASH_REMATCH[2]}"
        local port="${BASH_REMATCH[4]}"
        case "$host" in
            '*'|'+'|'0.0.0.0'|'[::]')
                if [[ "$scheme" == "https" ]]; then host="$(hostname | tr '[:upper:]' '[:lower:]')"; else host='127.0.0.1'; fi
                ;;
        esac
        if [[ -n "$port" ]]; then printf '%s://%s:%s/api/health' "$scheme" "$host" "$port"; else printf '%s://%s/api/health' "$scheme" "$host"; fi
        return 0
    fi
    return 1
}

wait_for_agent_health() {
    local url="$1" token="$2" expected_version="$3"
    local deadline=$((SECONDS + 30))
    local consecutive_successes=0 stable_process_id=""
    while (( SECONDS < deadline )); do
        if systemctl is-active --quiet "$UNIT_NAME"; then
            local headers_file="$tmp_dir/health.headers" body_file="$tmp_dir/health.body"
            : > "$headers_file"; : > "$body_file"
            if curl -fsS --connect-timeout 2 --max-time 3 -D "$headers_file" -o "$body_file" -H "Authorization: Bearer $token" "$url" 2>/dev/null; then
                local health_ok health_version process_id
                health_ok="$(grep -oP '"ok"\s*:\s*\K(true|false)' "$body_file" | head -1 || true)"
                health_version="$(awk -F': *' 'tolower($1)=="x-gameservercontrol-version" {gsub("\r", "", $2); print $2; exit}' "$headers_file")"
                process_id="$(awk -F': *' 'tolower($1)=="x-gameservercontrol-pid" {gsub("\r", "", $2); print $2; exit}' "$headers_file")"
                if [[ "$health_ok" == "true" && "$health_version" == "$expected_version" && "$process_id" =~ ^[1-9][0-9]*$ ]]; then
                    if [[ "$stable_process_id" == "$process_id" ]]; then consecutive_successes=$((consecutive_successes + 1)); else stable_process_id="$process_id"; consecutive_successes=1; fi
                else
                    consecutive_successes=0; stable_process_id=""
                fi
            else
                consecutive_successes=0; stable_process_id=""
            fi
        else
            consecutive_successes=0; stable_process_id=""
        fi
        (( consecutive_successes >= 3 )) && return 0
        sleep 2
    done
    return 1
}

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
cleanup() {
    rm -rf "$tmp_dir"
    [[ -n "${UPDATE_HEALTH_TOKEN_FILE:-}" ]] && rm -f "$UPDATE_HEALTH_TOKEN_FILE" 2>/dev/null || true
}
trap cleanup EXIT
curl -fsSL "$tar_url" -o "$tmp_dir/agent.tar.gz"
ok "Downloaded."

if systemctl is-active --quiet "$UNIT_NAME"; then
    step "Stopping existing $UNIT_NAME"
    systemctl stop "$UNIT_NAME"
    ok "Stopped."
fi

if [[ -d "$INSTALL_DIR" ]]; then
    step "Restricting existing Agent state before upgrade"
    chmod 700 "$INSTALL_DIR"
    for f in appsettings.json servers.json tokens.json public-relay.json managed-hosts.json managed-hosts.json.bak secret-protection.key appsettings.json.bak servers.json.bak; do
        [[ -f "$INSTALL_DIR/$f" ]] && chmod 600 "$INSTALL_DIR/$f"
    done
    ok "Existing Agent state permissions restricted."
fi

step "Installing into $INSTALL_DIR"
preserve_dir="$tmp_dir/preserved"
mkdir -p "$preserve_dir"
for f in appsettings.json servers.json tokens.json public-relay.json managed-hosts.json managed-hosts.json.bak secret-protection.key; do
    if [[ -f "$INSTALL_DIR/$f" ]]; then
        cp "$INSTALL_DIR/$f" "$preserve_dir/$f"
        skip "Preserving $f"
    fi
done

# Rollback previous Agent payload: keep the complete old directory until the replacement service is healthy.
rm -rf "$PREVIOUS_DIR"
if [[ -d "$INSTALL_DIR" ]] && [[ -f "$INSTALL_DIR/GameServerControl.Agent" ]]; then
    mv "$INSTALL_DIR" "$PREVIOUS_DIR"
    HAD_PREVIOUS=1
elif [[ -d "$INSTALL_DIR" ]]; then
    rm -rf "$INSTALL_DIR"
fi
mkdir -p "$INSTALL_DIR"
chmod 700 "$INSTALL_DIR"
tar -xzf "$tmp_dir/agent.tar.gz" -C "$INSTALL_DIR"

for f in appsettings.json servers.json tokens.json public-relay.json managed-hosts.json managed-hosts.json.bak secret-protection.key; do
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
chmod 700 "$INSTALL_DIR"
for f in appsettings.json servers.json tokens.json public-relay.json managed-hosts.json managed-hosts.json.bak secret-protection.key; do
    [[ -f "$INSTALL_DIR/$f" ]] && chmod 600 "$INSTALL_DIR/$f"
done

step "Installing systemd unit"
unit_src="$INSTALL_DIR/$UNIT_NAME"
[[ -f "$unit_src" ]] || fail "Tarball missing $UNIT_NAME."
cp "$unit_src" "/etc/systemd/system/$UNIT_NAME"
systemctl daemon-reload
systemctl enable "$UNIT_NAME" >/dev/null 2>&1 || true
bind="$(grep -oP '"Bind"\s*:\s*"\K[^"]+' "$INSTALL_DIR/appsettings.json" | head -1 || true)"
health_url="$(local_health_url "${bind:-http://127.0.0.1:5099}")" || fail "Agent:Bind is not a supported local HTTP(S) URL: ${bind:-<empty>}"
expected_version="${tag#v}"
UPDATE_HEALTH_TOKEN="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
printf '%s' "$UPDATE_HEALTH_TOKEN" > "$UPDATE_HEALTH_TOKEN_FILE"
chown "$SERVICE_USER:$SERVICE_USER" "$UPDATE_HEALTH_TOKEN_FILE"
chmod 600 "$UPDATE_HEALTH_TOKEN_FILE"
skip "Prepared restricted health-only update credential."

systemctl start "$UNIT_NAME" || true
if wait_for_agent_health "$health_url" "$UPDATE_HEALTH_TOKEN" "$expected_version"; then
    rm -f "$UPDATE_HEALTH_TOKEN_FILE"
    UPDATE_HEALTH_TOKEN=""
    ok "$UNIT_NAME passed authenticated health/version checks for $expected_version with a stable process."
    if [[ $HAD_PREVIOUS -eq 1 ]]; then
        rm -rf "$PREVIOUS_DIR"
        skip "Removed previous Agent payload after authenticated health verification."
    fi
else
    rm -f "$UPDATE_HEALTH_TOKEN_FILE"
    UPDATE_HEALTH_TOKEN=""
    if [[ $HAD_PREVIOUS -eq 1 ]] && [[ -d "$PREVIOUS_DIR" ]]; then
        warn "New Agent failed authenticated health/version stability checks. Rolling back the previous payload."
        systemctl stop "$UNIT_NAME" >/dev/null 2>&1 || true
        rm -rf "$INSTALL_DIR"
        mv "$PREVIOUS_DIR" "$INSTALL_DIR"
        old_unit="$INSTALL_DIR/$UNIT_NAME"
        if [[ -f "$old_unit" ]]; then
            cp "$old_unit" "/etc/systemd/system/$UNIT_NAME"
            systemctl daemon-reload
        fi
        systemctl start "$UNIT_NAME" || true
        if systemctl is-active --quiet "$UNIT_NAME"; then
            ok "Previous Agent payload restored and service restarted."
            fail "New Agent failed authenticated health/version stability checks. The previous Agent payload was restored."
        fi
        fail "New Agent failed health verification and rollback could not restart the previous service."
    fi
    systemctl stop "$UNIT_NAME" >/dev/null 2>&1 || true
    fail "New Agent failed authenticated health/version stability checks. Check: journalctl -u $UNIT_NAME -n 50"
fi

step "Secret state"
ok "API credentials and the Linux secret-protection key remain only in restricted local state files; plaintext tokens are not printed to service or installer stdout."

echo ""
echo "Installed $tag."
echo "  Service: systemctl status $UNIT_NAME"
echo "  Logs:    journalctl -u $UNIT_NAME -f"
echo "  Web UI:  http://<this-host>:5099/"
echo "  Config:  $INSTALL_DIR/appsettings.json"
echo ""
