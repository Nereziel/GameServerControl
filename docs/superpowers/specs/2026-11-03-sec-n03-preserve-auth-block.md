# SEC-N03 Preserve Active Authentication Blocks

## Intent

Keep the SEC-N01 availability fix — valid credentials must still authenticate from a throttled peer — without allowing a valid credential to erase the peer's active brute-force block.

## Problem

`TokenAuthHandler` now resolves valid credentials before enforcing the peer throttle. That prevents an attacker on a shared peer address from locking out a legitimate credential. However, both the normal token path and the update-health path call `AuthenticationFailureThrottle.RecordSuccess` after successful validation. `RecordSuccess` clears `BlockedUntil`, so any valid credential from the blocked peer immediately removes the block for subsequent invalid attempts.

A holder of a valid ReadOnly credential sharing the same direct peer can therefore repeatedly reset the peer throttle between invalid attempts against another credential.

## Required behavior

- Valid Admin, ReadOnly, and update-health credentials must continue to authenticate while the direct peer is actively blocked.
- A successful authentication while the peer is actively blocked must not clear or shorten that active block.
- The next missing or invalid credential from that peer must remain throttled until the original block expires.
- A successful authentication when the peer is not actively blocked must continue to clear accumulated, non-blocking authentication failures.
- Do not change the failure threshold, failure window, block duration, tracked-client bound, direct-peer keying, role semantics, or query-token rules.

## Approaches

### Chosen: suppress `RecordSuccess` only when the current request observed an active block

`TokenAuthHandler` already computes `throttled` before credential validation. Reuse that value and call `RecordSuccess` only when `throttled` is false, for both registry tokens and update-health credentials. This is the smallest change and keeps `AuthenticationFailureThrottle` semantics unchanged for every other caller.

### Rejected: change `AuthenticationFailureThrottle.RecordSuccess`

Making the throttle itself preserve active blocks would also work, but changes the class-wide meaning of `RecordSuccess` and introduces time-dependent behavior into a method whose current contract is an unconditional reset.

### Rejected: track failures per credential plus peer

This would reduce shared-peer coupling but materially changes the throttle model and would require a broader design for unknown credentials, token rotation, and memory bounds.

## Verification

Extend authentication integration coverage so a peer is blocked by invalid attempts, then successfully authenticates with the Admin token, then submits another invalid token. The final attempt must still fail as `authentication temporarily throttled`. Existing SEC-N01 coverage must continue proving the valid token itself succeeds during the block.
