# SEC-N01 Authentication Throttle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve brute-force throttling while ensuring a valid credential from a blocked peer can authenticate and clear the peer's failure state.

**Architecture:** Keep `AuthenticationFailureThrottle` unchanged. Change only `TokenAuthHandler` ordering so bearer credentials are parsed and valid credentials are resolved before a peer block can reject them; apply the existing throttle result only to missing/invalid credentials. Prove the regression at the authentication-handler boundary with a real `TokenRegistry`.

**Tech Stack:** .NET 8, ASP.NET Core authentication, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-03-sec-n01-auth-throttle.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Keep direct peer IP as the throttle key; do not trust forwarded headers.
- Do not change thresholds, windows, block duration, or tracked-client capacity.
- Preserve Admin, ReadOnly, SignalR query-token, and update-health authentication semantics.

## Review Focus

- A valid Admin token after a peer is blocked must authenticate and clear the block.
- Invalid credentials from an already blocked peer must still be rejected as throttled.
- Missing credentials must not bypass throttling.
- A valid update-health credential must remain usable after invalid attempts from the same peer.
- Failure state must remain isolated by direct peer address.

---

### Task 1: Reorder authentication throttling around credential validation

**Files:**
- Modify: `tests/GameServerControl.Agent.IntegrationTests/AuthenticationIntegrationTests.cs`
- Modify: `src/GameServerControl.Agent/Auth/TokenAuthHandler.cs`

**Interfaces:**
- Consumes: `AuthenticationFailureThrottle.Shared`, `TokenRegistry.Lookup(string)`, `TokenAuthHandler.InitializeAsync(...)`.
- Produces: authentication behavior where valid credentials bypass an existing peer block and call `RecordSuccess`.

- [ ] **Step 1: Write the failing regression test**

Add `Valid_admin_token_authenticates_after_same_peer_is_throttled`. Construct real `TokenAuthHandler` instances with one fixed documentation-only IP, submit eight invalid bearer credentials to trigger the existing block, then submit `AgentTestFactory.AdminToken` from the same IP and assert authentication succeeds.

- [ ] **Step 2: Run the authentication test suite and verify RED**

Run: `dotnet test tests/GameServerControl.Agent.IntegrationTests/GameServerControl.Agent.IntegrationTests.csproj -c Release --filter AuthenticationIntegrationTests`

Expected: the new regression test fails because the valid request is rejected before `TokenRegistry.Lookup`.

- [ ] **Step 3: Implement the minimal production change**

In `TokenAuthHandler.HandleAuthenticateAsync`, parse the presented credential first. Permit valid update-health and registry credentials to authenticate and clear peer failure state regardless of an existing block. For missing or invalid credentials, preserve the current blocked-peer `Retry-After` behavior and failure recording.

- [ ] **Step 4: Run focused and full test suites and verify GREEN**

Run focused: `dotnet test tests/GameServerControl.Agent.IntegrationTests/GameServerControl.Agent.IntegrationTests.csproj -c Release --filter AuthenticationIntegrationTests`

Run full: `dotnet test -c Release`

Expected: both pass with zero failures.

- [ ] **Step 5: Commit**

Commit test and production change with a security-fix message, then verify branch CI before opening the PR.
