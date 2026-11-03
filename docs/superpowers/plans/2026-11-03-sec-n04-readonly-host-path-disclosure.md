# SEC-N04 ReadOnly Host Path Disclosure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent ReadOnly tokens from learning administrative host filesystem paths while preserving monitoring and safe mod visibility.

**Architecture:** Extend the existing centralized ReadOnly GET policy in `RoleEnforcementMiddleware`. Deny discovery and install-job lookup before their handlers execute, while buffering the exact per-server mod-list response and replacing only `modsFolder` with null. Admin requests continue through unchanged. Add runtime and static contracts for both behaviors.

**Tech Stack:** .NET 8, ASP.NET Core minimal APIs, xUnit integration tests, GitHub Actions validation scripts.

**Spec:** `docs/superpowers/specs/2026-11-03-sec-n04-readonly-host-path-disclosure.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Preserve ReadOnly monitoring/status/history and safe mod metadata.
- Preserve Admin discovery, install progress and full mod-folder diagnostics.
- Do not expose or log any token/password/secret values.
- Do not change write authorization semantics.

## Review Focus

- ReadOnly denial must happen before nonexistent install-job lookup, producing 403 rather than 404.
- Admin callers must retain discovery and install-job behavior.
- ReadOnly mod listing must redact only the absolute folder, not the mod list itself.
- Unsupported mod managers must keep their existing unsupported response behavior.
- Relative mod-owned file paths remain relative and readable.

---

### Task 1: Prove current ReadOnly disclosures

**Files:**
- Modify: `tests/GameServerControl.Agent.IntegrationTests/AuthenticationIntegrationTests.cs`

**Interfaces:**
- Produces: endpoint-level regression tests using real Admin and ReadOnly authentication.

- [ ] **Step 1: Add failing discovery authorization test**

Assert ReadOnly `GET /api/discover` is 403 and Admin `GET /api/discover` is 200.

- [ ] **Step 2: Add failing install-progress authorization test**

Assert ReadOnly `GET /api/servers/install/does-not-exist` is 403 while Admin receives 404.

- [ ] **Step 3: Add failing mod-folder redaction test**

Use `AgentTestFactory(alphaUsesLogProfile: true)` so `alpha` is Valheim/BareMetal. Assert Admin `/api/servers/alpha/mods` exposes the expected `BepInEx/plugins` folder while ReadOnly returns the same response class with `modsFolder` null.

- [ ] **Step 4: Run runtime integration and verify RED**

Run: `dotnet test tests/GameServerControl.Agent.IntegrationTests/GameServerControl.Agent.IntegrationTests.csproj -c Release -p:WarningsAsErrors=CA1416`

Expected: the three new ReadOnly assertions fail on current behavior.

### Task 2: Enforce minimal ReadOnly exposure

**Files:**
- Modify: `src/GameServerControl.Agent/Auth/RoleEnforcementMiddleware.cs`

**Interfaces:**
- ReadOnly exact `GET /api/discover` and single-segment `GET /api/servers/install/{jobId}` are rejected before `_next`.
- ReadOnly exact per-server `/mods` response retains normal content except `modsFolder = null`.
- Admin behavior remains unchanged because these branches run only for ReadOnly GETs.

- [ ] **Step 1: Deny discovery and install-job details before handlers**

Add exact path checks inside the existing ReadOnly GET block. Keep the install-job matcher scoped to one non-empty path segment after `/api/servers/install/`.

- [ ] **Step 2: Redact ReadOnly mod folder**

Use the existing config-redaction response-buffer pattern for exact per-server `/mods`: parse successful JSON, set `modsFolder` to null, and pass through non-success/unrecognized responses unchanged.

- [ ] **Step 3: Run integration tests and verify GREEN**

Expected: full integration project passes with CA1416 promoted to error.

### Task 3: Pin the security contract and verify CI

**Files:**
- Modify: `scripts/validate-security-access.mjs`

**Interfaces:**
- Static validator requires discovery/install-job ReadOnly denial and explicit mod-folder nulling.

- [ ] **Step 1: Extend the static security contract**

Require the exact discovery check, scoped install-job matcher, mod-list response redaction call and `root["modsFolder"] = null` assignment.

- [ ] **Step 2: Run all PR workflows**

Require Build, Runtime integration tests, Version contract, Supply-chain audit contract, PERF contract, ARCH-01 and BUG-02 to succeed before merge.

- [ ] **Step 3: Review final diff**

Confirm changes are limited to ReadOnly GET exposure/redaction, security tests/contract and SEC-N04 documentation.
