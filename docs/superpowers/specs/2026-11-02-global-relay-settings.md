# Global Relay Settings Design

## Intent

Move Agent-wide public Relay connection settings out of each server's Network workspace into one global Settings view. Per-server Network should only control whether that server is exposed and which public ports map to its game ports.

## Goals

- Add a top-level Settings view in the existing Agent Web UI and the staged Cloudflare Web UI.
- Add authenticated Agent API endpoints to read and update the global Relay URL, API token, optional target Tailscale IP, and public display address.
- Keep the Relay API token write-only in browser-visible responses.
- Preserve existing `public-relay.json` storage and existing release/update preservation behavior.
- Keep existing per-server Relay endpoints backward compatible for older clients.
- Remove global Relay connection inputs from the per-server Network workspace.
- Keep per-server enable/disable and public-port mapping behavior unchanged.
- Let the existing periodic relay reconciliation apply a newly saved global configuration to running servers.

## Non-goals

- Do not change VPS Relay routing, iptables, Tailscale, or provider firewall behavior.
- Do not add Cloudflare account configuration here.
- Do not add a general Agent configuration editor.
- Do not expose or return the saved Relay API token.

## Approach

Use the existing `PublicRelayManager` as the single source of truth. Add explicit global status/settings models and manager methods, expose them as `/api/settings/public-relay`, then add a top-level Settings panel. The per-server Network relay card reads the same server status as today but links to global Settings when the Relay is not configured and submits only `enabled` plus `publicPorts`.

## Compatibility

The existing `/api/servers/{id}/relay` PUT continues accepting legacy global fields so older clients remain functional. New UI code never sends them.
