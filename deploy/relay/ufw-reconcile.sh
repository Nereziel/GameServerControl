#!/usr/bin/env bash

GSC_RELAY_UFW_COMMENT='GameServerControl Relay API'

gsc_delete_relay_api_ufw_rule() {
  local iface="${1:-}"
  local address="${2:-}"
  local port="${3:-}"

  [[ -n "$iface" && -n "$address" && -n "$port" ]] || return 0

  # Delete only the exact GameServerControl-owned rule. A missing rule is fine.
  ufw delete allow in on "$iface" to "$address" port "$port" proto tcp \
    comment "$GSC_RELAY_UFW_COMMENT" >/dev/null 2>&1 || true
}

gsc_reconcile_relay_api_ufw() {
  local current_iface="${1:-}"
  local current_address="${2:-}"
  local current_port="${3:-}"
  local previous_iface="${4:-}"
  local previous_address="${5:-}"
  local previous_port="${6:-}"

  command -v ufw >/dev/null 2>&1 || return 0
  [[ -n "$current_iface" && -n "$current_address" && -n "$current_port" ]] || return 0

  # If the endpoint moved, clean the previous managed rule first. Never issue a
  # broad delete because administrators may have unrelated rules on this port.
  if [[ -n "$previous_iface" && -n "$previous_address" && -n "$previous_port" ]] &&
     [[ "$previous_iface" != "$current_iface" || "$previous_address" != "$current_address" || "$previous_port" != "$current_port" ]]; then
    gsc_delete_relay_api_ufw_rule "$previous_iface" "$previous_address" "$previous_port"
  fi

  # Reconcile the current rule as delete + add so repeated installs stay
  # idempotent even if UFW's duplicate detection changes across versions.
  gsc_delete_relay_api_ufw_rule "$current_iface" "$current_address" "$current_port"

  if ufw status 2>/dev/null | grep -q '^Status: active'; then
    ufw allow in on "$current_iface" to "$current_address" port "$current_port" proto tcp \
      comment "$GSC_RELAY_UFW_COMMENT" >/dev/null
  fi
}
