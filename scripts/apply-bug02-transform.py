from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text()
    if old not in text:
        raise SystemExit(f"marker not found in {path}: {old[:100]!r}")
    p.write_text(text.replace(old, new, 1))


# SecurityCheck behavior coverage.
security = Path("tools/GameServerControl.SecurityCheck/Program.cs")
text = security.read_text()
marker = 'var root = Path.Combine(Path.GetTempPath(), "gsc-security-check-" + Guid.NewGuid().ToString("n"));'
helper = r'''static void VerifyUpdateHealthToken(string root)
{
    var probeRoot = Path.Combine(root, "update-health");
    Directory.CreateDirectory(probeRoot);
    const string token = "update-health-token-0123456789abcdef";

    Require(!UpdateHealthToken.IsValid(probeRoot, token), "Missing update-health credential was accepted.");
    File.WriteAllText(Path.Combine(probeRoot, UpdateHealthToken.FileName), token);
    Require(UpdateHealthToken.IsValid(probeRoot, token), "Exact update-health credential was rejected.");
    Require(!UpdateHealthToken.IsValid(probeRoot, token + "x"), "Suffixed update-health credential was accepted.");
    Require(!UpdateHealthToken.IsValid(probeRoot, "wrong-update-health-token"), "Wrong update-health credential was accepted.");
    File.WriteAllText(Path.Combine(probeRoot, UpdateHealthToken.FileName), new string('x', 513));
    Require(!UpdateHealthToken.IsValid(probeRoot, new string('x', 513)), "Oversized update-health credential was accepted.");
}

'''
if marker not in text:
    raise SystemExit("SecurityCheck insertion marker missing")
text = text.replace(marker, helper + marker, 1)
old_security_tail = '''    VerifyAuthenticationThrottle();
    VerifyPathContainment(root);
    VerifySecretFilePermissions(root);
    Console.WriteLine("Security token storage, authentication throttle, path containment, and secret file permission checks OK.");'''
new_security_tail = '''    VerifyAuthenticationThrottle();
    VerifyPathContainment(root);
    VerifySecretFilePermissions(root);
    VerifyUpdateHealthToken(root);
    Console.WriteLine("Security token storage, authentication throttle, path containment, secret file permission, and update-health token checks OK.");'''
if old_security_tail not in text:
    raise SystemExit("SecurityCheck call marker missing")
security.write_text(text.replace(old_security_tail, new_security_tail, 1))

# Windows helper functions and startup gate.
win = Path("deploy/windows/install.ps1")
text = win.read_text()
acl_marker = '''function Set-AgentDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }

    # Stable well-known SIDs avoid localized account-name issues. The managed service runs
    # as LocalSystem, so these correspond to SYSTEM:(OI)(CI)F and
    # BUILTIN\\Administrators:(OI)(CI)F and are inherited by secret state files.
    $aclOutput = & icacls.exe $Path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /T /C 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Failed to restrict Agent directory ACL: $($aclOutput -join ' | ')" }
}
'''
helpers = acl_marker + r'''
function New-UpdateHealthToken {
    param([Parameter(Mandatory = $true)][string]$Path)
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    [IO.File]::WriteAllText($Path, $token, [Text.Encoding]::ASCII)
    return $token
}

function Get-LocalAgentHealthUrl {
    param([string]$Bind)
    if ([string]::IsNullOrWhiteSpace($Bind)) { $Bind = 'http://127.0.0.1:5099' }
    $candidate = $Bind.Trim()
    if ($candidate -match '^(https?)://(\*|\+|0\.0\.0\.0|\[::\])(?::([0-9]+))?/?$') {
        $scheme = $Matches[1]
        $port = $Matches[3]
        $localHost = if ($scheme -eq 'https') { [Environment]::MachineName.ToLowerInvariant() } else { '127.0.0.1' }
        $candidate = if ($port) { "${scheme}://${localHost}:$port" } else { "${scheme}://${localHost}" }
    }
    try { $uri = [Uri]$candidate } catch { throw "Agent:Bind is not a valid absolute URL: $Bind" }
    if ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https') { throw "Agent:Bind must use http or https: $Bind" }
    $builder = New-Object UriBuilder($uri)
    $builder.Path = '/api/health'
    $builder.Query = ''
    $builder.Fragment = ''
    return $builder.Uri.AbsoluteUri
}

function Test-AgentUpdateHealth {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Token,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [int]$TimeoutSeconds = 30,
        [int]$RequiredConsecutiveSuccesses = 3
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $consecutiveSuccesses = 0
    $stableProcessId = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $service = Get-Service -Name $svcName -ErrorAction SilentlyContinue
        if (-not $service -or $service.Status -ne 'Running') {
            $consecutiveSuccesses = 0
            $stableProcessId = $null
        } else {
            try {
                $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -Method Get -TimeoutSec 3 -Headers @{ Authorization = "Bearer $Token" }
                $body = $response.Content | ConvertFrom-Json
                $version = [string]$response.Headers['X-GameServerControl-Version']
                $processId = [string]$response.Headers['X-GameServerControl-Pid']
                $valid = $response.StatusCode -eq 200 -and $body.ok -eq $true -and $version -eq $ExpectedVersion -and $processId -match '^[1-9][0-9]*$'
                if ($valid) {
                    if ($stableProcessId -eq $processId) { $consecutiveSuccesses += 1 }
                    else { $stableProcessId = $processId; $consecutiveSuccesses = 1 }
                } else {
                    $consecutiveSuccesses = 0
                    $stableProcessId = $null
                }
            } catch {
                $consecutiveSuccesses = 0
                $stableProcessId = $null
            }
        }
        if ($consecutiveSuccesses -ge $RequiredConsecutiveSuccesses) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}
'''
if acl_marker not in text:
    raise SystemExit("Windows ACL marker missing")
text = text.replace(acl_marker, helpers, 1)

old_win = r'''Write-Step "Starting $svcName"
try {
    Start-Service -Name $svcName
    (Get-Service -Name $svcName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
} catch { Write-Warn2 "Service failed to start: $($_.Exception.Message)" }
$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if (-not $svc -or $svc.Status -ne 'Running') {
    if ($hadPreviousAgent -and (Test-Path $previousAgentDir)) {
        Write-Warn2 'New Agent did not become healthy. Rolling back the previous payload.'
        Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue
        if (Test-Path $agentDir) { Remove-Item $agentDir -Recurse -Force }
        Move-Item -LiteralPath $previousAgentDir -Destination $agentDir
        try {
            Start-Service -Name $svcName
            (Get-Service -Name $svcName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
            Write-Ok 'Previous Agent payload restored and service restarted.'
        } catch {
            throw "New Agent failed to start and rollback could not restart the previous service: $($_.Exception.Message)"
        }
        throw 'New Agent failed to start. The previous Agent payload was restored.'
    }
    throw 'Service is not running after installation. Inspect the Application Event Log or run the Agent EXE directly.'
}
Write-Ok 'Service running.'
if ($hadPreviousAgent -and (Test-Path $previousAgentDir)) {
    Remove-Item $previousAgentDir -Recurse -Force
    Write-Skip 'Removed previous Agent payload after successful startup.'
}
'''
new_win = r'''$expectedVersion = if ($tag.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) { $tag.Substring(1) } else { $tag }
$healthUrl = Get-LocalAgentHealthUrl -Bind ([string]$cfgObj.Agent.Bind)
$updateHealthTokenPath = Join-Path $agentDir '.update-health-token'
$updateHealthToken = New-UpdateHealthToken -Path $updateHealthTokenPath
Set-AgentDirectoryAcl -Path $agentDir
Write-Skip 'Prepared restricted health-only update credential.'

Write-Step "Starting $svcName"
try {
    Start-Service -Name $svcName
    (Get-Service -Name $svcName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
} catch { Write-Warn2 "Service start did not reach Running before health verification: $($_.Exception.Message)" }

$healthPassed = Test-AgentUpdateHealth -Url $healthUrl -Token $updateHealthToken -ExpectedVersion $expectedVersion -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 3
Remove-Item -LiteralPath $updateHealthTokenPath -Force -ErrorAction SilentlyContinue
$updateHealthToken = $null

if (-not $healthPassed) {
    if ($hadPreviousAgent -and (Test-Path $previousAgentDir)) {
        Write-Warn2 'New Agent failed authenticated health/version stability checks. Rolling back the previous payload.'
        Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue
        if (Test-Path $agentDir) { Remove-Item $agentDir -Recurse -Force }
        Move-Item -LiteralPath $previousAgentDir -Destination $agentDir
        try {
            Start-Service -Name $svcName
            (Get-Service -Name $svcName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
            Write-Ok 'Previous Agent payload restored and service restarted.'
        } catch {
            throw "New Agent failed health verification and rollback could not restart the previous service: $($_.Exception.Message)"
        }
        throw 'New Agent failed authenticated health/version stability checks. The previous Agent payload was restored.'
    }
    Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue
    throw 'New Agent failed authenticated health/version stability checks. Inspect the Application Event Log or run the Agent EXE directly.'
}

Write-Ok "Authenticated health/version gate passed for $expectedVersion with a stable process."
if ($hadPreviousAgent -and (Test-Path $previousAgentDir)) {
    Remove-Item $previousAgentDir -Recurse -Force
    Write-Skip 'Removed previous Agent payload after authenticated health verification.'
}
'''
if old_win not in text:
    raise SystemExit("Windows startup block missing")
win.write_text(text.replace(old_win, new_win, 1))

# Linux release installer helpers + gate.
linux = Path("deploy/linux/install-from-release.sh")
text = linux.read_text()
text = text.replace('HAD_PREVIOUS=0\n', 'HAD_PREVIOUS=0\nUPDATE_HEALTH_TOKEN_FILE="$INSTALL_DIR/.update-health-token"\nUPDATE_HEALTH_TOKEN=""\n', 1)
text = text.replace('for tool in curl tar systemctl; do', 'for tool in curl tar systemctl grep awk od tr hostname; do', 1)
text = text.replace("trap 'rm -rf \"$tmp_dir\"' EXIT", '''cleanup() {
    rm -rf "$tmp_dir"
    [[ -n "${UPDATE_HEALTH_TOKEN_FILE:-}" ]] && rm -f "$UPDATE_HEALTH_TOKEN_FILE" 2>/dev/null || true
}
trap cleanup EXIT''', 1)
linux_marker = '''ok "curl, tar, systemctl all present."
ok "Self-contained release — system .NET runtime is not required."
'''
linux_helpers = linux_marker + r'''
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
'''
if linux_marker not in text:
    raise SystemExit("Linux helper marker missing")
text = text.replace(linux_marker, linux_helpers, 1)
old_linux = r'''systemctl start "$UNIT_NAME" || true
sleep 2
if systemctl is-active --quiet "$UNIT_NAME"; then
    ok "$UNIT_NAME running."
    if [[ $HAD_PREVIOUS -eq 1 ]]; then
        rm -rf "$PREVIOUS_DIR"
        skip "Removed previous Agent payload after successful startup."
    fi
else
    if [[ $HAD_PREVIOUS -eq 1 ]] && [[ -d "$PREVIOUS_DIR" ]]; then
        warn "New Agent did not become healthy. Rolling back the previous payload."
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
            fail "New Agent failed to start. The previous Agent payload was restored."
        fi
        fail "New Agent failed to start and rollback could not restart the previous service."
    fi
    fail "Service did not become active. Check: journalctl -u $UNIT_NAME -n 50"
fi
'''
new_linux = r'''bind="$(grep -oP '"Bind"\s*:\s*"\K[^"]+' "$INSTALL_DIR/appsettings.json" | head -1 || true)"
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
'''
if old_linux not in text:
    raise SystemExit("Linux startup block missing")
linux.write_text(text.replace(old_linux, new_linux, 1))
