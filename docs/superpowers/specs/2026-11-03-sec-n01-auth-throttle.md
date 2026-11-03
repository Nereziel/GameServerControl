# SEC-N01 authentication throttle design

## Problem

`TokenAuthHandler` currently checks `AuthenticationFailureThrottle` before parsing and validating the presented bearer credential. Once a peer IP is blocked by repeated invalid authentication attempts, a later valid Admin, ReadOnly, or update-health credential from the same peer is rejected until the block expires.

This is a local availability/security regression because multiple trusted and untrusted local paths can share the Agent peer address (notably loopback-oriented deployments).

## Required behavior

- Repeated invalid authentication attempts must remain rate-limited by direct peer address.
- A valid bearer credential must still authenticate even when that peer currently has a failure block.
- Successful authentication must clear that peer's failure state as it does today.
- The short-lived update-health credential must retain the same behavior as other valid credentials.
- Do not change the configured failure threshold, time window, block duration, or tracked-client bound as part of this fix.
- Do not trust proxy forwarding headers for the throttle key; continue using the direct connection peer address.

## Verification

Add a regression test that records enough invalid attempts from one peer to trigger the existing block, then presents the valid legacy Admin token from the same peer and requires authentication success. Existing authentication and integration tests must remain green.
