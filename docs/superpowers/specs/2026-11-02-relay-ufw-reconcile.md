# Relay UFW Rule Reconciliation Design

## Goal

Keep the GameServerControl Relay API reachable only over Tailscale without leaving stale UFW allow rules behind when the Relay Tailscale IP, interface, or API port changes across installer runs.

## Current behavior

`deploy/relay/install.sh` detects the current Tailscale IPv4/interface and, when UFW is active, adds an allow rule for the Relay API. The installer then overwrites `/etc/gameservercontrol-relay.env`. It does not remember or remove the previous rule, so an old address can remain allowed after a Tailscale address or port change.

## Design

Before overwriting the Relay env file, read the previous `Relay__Bind` and `Relay__TailscaleInterface` values when available. Derive the previous API address and port from the bind URL.

Move UFW reconciliation into a small shell helper that is independently testable. Reconciliation will:

1. Do nothing when `ufw` is unavailable.
2. Remove the exact previous GameServerControl Relay API rule, using the same interface/address/port and the `GameServerControl Relay API` comment.
3. Remove the exact current rule as well, so repeated installer runs are idempotent and do not accumulate duplicates.
4. Add exactly one current rule only when UFW is active.
5. Never delete rules that do not match the GameServerControl Relay API rule signature and comment.

The Relay service continues to bind only to its detected Tailscale IPv4. No public-interface rule is added.

## Packaging

The helper must be included in Linux Relay CI artifacts and release tarballs because `install.sh` sources it at runtime.

## Verification

A shell regression test with a stubbed `ufw` command verifies old-rule deletion, current-rule deduplication, active/inactive behavior, and absence of broad deletes. CI runs the test on Linux before publishing Relay artifacts.
