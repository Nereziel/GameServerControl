# BUG-02 Authenticated Update Health Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent Windows and Linux release updaters from deleting the rollback payload until the replacement Agent proves an authenticated `/api/health` response from the expected release and remains healthy across consecutive probes.

**Architecture:** The installer creates a short-lived random bearer token in a restricted `.update-health-token` file inside the replacement Agent directory. `TokenAuthHandler` accepts that credential only for `/api/health`; the health response reports the running assembly version and PID. Each release installer polls the local Agent for up to 30 seconds and requires three consecutive authenticated responses with `ok=true` and the expected version before deleting `Agent.previous`; any failure removes the temporary credential and follows the existing rollback path.

**Tech Stack:** ASP.NET Core 8, C#, PowerShell, Bash, Node.js contract tests, GitHub Actions.

**Spec:** Original audit finding BUG-02: rollback must verify authenticated `/api/health`, expected version, and process stability rather than only service state.

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Do not print or persist reusable normal API credentials for health probing.
- Temporary update credential must authenticate only `/api/health` and be removed after the probe.
- Release version comparison uses the resolved release tag with one leading `v` removed.
- Require three consecutive successful probes inside a 30-second window before deleting the rollback payload.

## Review Focus

- Fresh install with no existing Agent must not require an existing API token.
- Encrypted `Agent:ApiToken` at rest must not need to be decrypted by shell scripts.
- Wrong update-health token must not authenticate, and the correct token must not grant access to other API routes.
- Custom `Agent:Bind` host/port must still yield a local probe URL without probing a wildcard address.
- Failed or wrong-version health responses must retain or restore the previous payload rather than deleting it.

---

### Task 1: Add failing BUG-02 contracts

**Files:**
- Create: `scripts/validate-update-health.mjs`
- Modify: `.github/workflows/build.yml`
- Modify: `.github/workflows/release.yml`

- [ ] Write source/ordering contracts for health response version, health-only temporary bearer authentication, Windows and Linux authenticated polling, 30-second/three-success stability, and rollback-payload deletion only after the gate.
- [ ] Run the contract and verify RED on the current implementation.

### Task 2: Add scoped update-health authentication and versioned health response

**Files:**
- Create: `src/GameServerControl.Agent/Auth/UpdateHealthToken.cs`
- Modify: `src/GameServerControl.Agent/Auth/TokenAuthHandler.cs`
- Modify: `src/GameServerControl.Agent/Notifications/UpdateChecker.cs`
- Modify: `src/GameServerControl.Agent/Program.cs`
- Modify: `tools/GameServerControl.SecurityCheck/GameServerControl.SecurityCheck.csproj`
- Modify: `tools/GameServerControl.SecurityCheck/Program.cs`

- [ ] Add behavior tests for exact temporary-token matching and missing/wrong-token rejection.
- [ ] Implement constant-time token comparison against the restricted file and scope its use to exact `/api/health` requests.
- [ ] Expose the running assembly version and include `version` and `pid` in the authenticated health payload.
- [ ] Run SecurityCheck and BUG-02 contract GREEN.

### Task 3: Gate rollback cleanup on authenticated stable health

**Files:**
- Modify: `deploy/windows/install.ps1`
- Modify: `deploy/linux/install-from-release.sh`
- Test: `scripts/validate-update-health.mjs`
- Test: `scripts/validate-windows-updater.ps1`

- [ ] Generate a short-lived random update-health credential in the replacement Agent directory with existing restrictive permissions.
- [ ] Derive a local probe URL from `Agent:Bind`, replacing wildcard hosts with loopback.
- [ ] Poll authenticated `/api/health` for up to 30 seconds and require three consecutive `ok=true`, expected-version responses while service remains running.
- [ ] Delete the temporary credential before success cleanup; on failure, delete it and execute the existing rollback.
- [ ] Run Windows parser, Linux syntax, BUG-02 contract, SecurityCheck, then full Windows/Linux CI.
