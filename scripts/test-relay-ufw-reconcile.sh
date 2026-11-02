#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
HELPER="$ROOT/deploy/relay/ufw-reconcile.sh"

if [[ ! -f "$HELPER" ]]; then
  echo "Missing Relay UFW reconciliation helper: $HELPER" >&2
  exit 1
fi

# shellcheck source=/dev/null
source "$HELPER"

UFW_ACTIVE=1
UFW_CALLS=()
ufw() {
  UFW_CALLS+=("$*")
  if [[ "${1:-}" == "status" ]]; then
    if [[ "$UFW_ACTIVE" == "1" ]]; then
      echo "Status: active"
    else
      echo "Status: inactive"
    fi
  fi
  return 0
}

fail() { echo "FAIL: $*" >&2; exit 1; }
contains_call() {
  local expected="$1" call
  for call in "${UFW_CALLS[@]}"; do
    [[ "$call" == "$expected" ]] && return 0
  done
  return 1
}
count_calls_starting() {
  local prefix="$1" count=0 call
  for call in "${UFW_CALLS[@]}"; do
    [[ "$call" == "$prefix"* ]] && ((count+=1))
  done
  echo "$count"
}

COMMENT="GameServerControl Relay API"
OLD_DELETE="delete allow in on tailscale0 to 100.100.10.9 port 5100 proto tcp comment $COMMENT"
CURRENT_DELETE="delete allow in on tailscale0 to 100.100.10.10 port 5100 proto tcp comment $COMMENT"
CURRENT_ALLOW="allow in on tailscale0 to 100.100.10.10 port 5100 proto tcp comment $COMMENT"

# Address changes: remove the old managed rule, deduplicate the current one, then add exactly one current rule.
UFW_CALLS=()
UFW_ACTIVE=1
gsc_reconcile_relay_api_ufw tailscale0 100.100.10.10 5100 tailscale0 100.100.10.9 5100
contains_call "$OLD_DELETE" || fail "old managed UFW rule was not removed"
contains_call "$CURRENT_DELETE" || fail "current managed UFW rule was not deduplicated"
contains_call "$CURRENT_ALLOW" || fail "current managed UFW rule was not added"
[[ "$(count_calls_starting 'allow in on ')" == "1" ]] || fail "expected exactly one allow rule"

# Same values: still remove/re-add the managed rule, but never accumulate duplicates.
UFW_CALLS=()
gsc_reconcile_relay_api_ufw tailscale0 100.100.10.10 5100 tailscale0 100.100.10.10 5100
[[ "$(count_calls_starting 'delete allow in on ')" == "1" ]] || fail "same rule should be deleted only once"
[[ "$(count_calls_starting 'allow in on ')" == "1" ]] || fail "same rule should be added only once"

# Inactive UFW: clean old/current managed rules, but do not create a new allow rule.
UFW_CALLS=()
UFW_ACTIVE=0
gsc_reconcile_relay_api_ufw tailscale0 100.100.10.10 5100 tailscale0 100.100.10.9 5100
contains_call "$OLD_DELETE" || fail "inactive UFW should still clean the old managed rule"
contains_call "$CURRENT_DELETE" || fail "inactive UFW should still clean the current managed rule"
[[ "$(count_calls_starting 'allow in on ')" == "0" ]] || fail "inactive UFW must not add an allow rule"

# Missing previous state is valid: reconcile only the current managed rule.
UFW_CALLS=()
UFW_ACTIVE=1
gsc_reconcile_relay_api_ufw tailscale0 100.100.10.10 5100 "" "" ""
[[ "$(count_calls_starting 'delete allow in on ')" == "1" ]] || fail "missing previous state should only delete the current managed rule"
contains_call "$CURRENT_ALLOW" || fail "current rule should still be added without previous state"

# Every delete must be scoped to our comment; broad deletes could remove administrator-owned rules.
for call in "${UFW_CALLS[@]}"; do
  if [[ "$call" == delete\ * && "$call" != *"comment $COMMENT"* ]]; then
    fail "broad UFW delete detected: $call"
  fi
done

echo "Relay UFW reconciliation tests passed."
