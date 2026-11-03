# SEC-N03 Preserve Authentication Block Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep valid credentials usable during a peer throttle without letting successful authentication erase an active brute-force block.

**Architecture:** Reuse the `throttled` decision already computed by `TokenAuthHandler`. Successful registry and update-health credentials still authenticate during an active block, but `RecordSuccess` runs only when the request was not already blocked, preserving the current block while retaining normal pre-block failure reset behavior.

**Tech Stack:** .NET 8, ASP.NET Core authentication, xUnit, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-03-sec-n03-preserve-auth-block.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Keep valid Admin, ReadOnly, and update-health credentials usable during an active peer block.
- Do not change thresholds, windows, block duration, tracked-client capacity, direct-peer keying, roles, or query-token rules.
- Preserve successful-auth reset behavior when no active block exists.

## Review Focus

- A valid Admin token during a block succeeds but does not clear the block.
- An invalid credential immediately after that valid request is still throttled.
- Successful authentication before a block still clears accumulated failures.
- Update-health authentication follows the same preserve-active-block rule.
- Invalid credentials from an already blocked peer remain 429/throttled behavior through the existing challenge path.

---

### Task 1: Preserve active peer blocks across successful authentication

**Files:**
- Modify: `tests/GameServerControl.Agent.IntegrationTests/AuthenticationIntegrationTests.cs`
- Modify: `src/GameServerControl.Agent/Auth/TokenAuthHandler.cs`

**Interfaces:**
- Consumes: existing local `throttled` boolean in `TokenAuthHandler.HandleAuthenticateAsync` and `AuthenticationFailureThrottle.RecordSuccess(string)`.
- Produces: successful authentication that does not clear an already active peer block.

- [ ] **Step 1: Write the failing regression test**

Add `Valid_admin_token_does_not_clear_existing_peer_throttle`. Trigger the current eight-failure block for a unique documentation-only peer, authenticate successfully with `AgentTestFactory.AdminToken`, then authenticate once more with an invalid token. Assert the valid request succeeds and the final failure message contains `authentication temporarily throttled`.

- [ ] **Step 2: Run authentication integration tests and verify RED**

Run: `dotnet test tests/GameServerControl.Agent.IntegrationTests/GameServerControl.Agent.IntegrationTests.csproj -c Release --filter AuthenticationIntegrationTests`

Expected: the new test fails because successful authentication currently calls `RecordSuccess`, clearing the active block; the final invalid request reports `invalid token` rather than `authentication temporarily throttled`.

- [ ] **Step 3: Implement the minimal production change**

In both successful paths in `TokenAuthHandler.HandleAuthenticateAsync` — update-health and registry token — call `_failureThrottle.RecordSuccess(clientKey)` only when `clientKey` is non-null and `throttled` is false. Do not change authentication success or role claims.

- [ ] **Step 4: Verify focused and full PR CI GREEN**

Run the authentication integration suite, then require all repository PR workflows to succeed on Linux and Windows.

Expected: the new regression and existing SEC-N01 test both pass, with no workflow failures.

- [ ] **Step 5: Review and merge**

Review the final diff for auth semantics only. Squash-merge after all PR workflows are green.
