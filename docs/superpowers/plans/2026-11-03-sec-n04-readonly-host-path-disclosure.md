# SEC-N04 ReadOnly Host Path Disclosure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent ReadOnly tokens from learning administrative host filesystem paths while preserving monitoring and safe mod visibility.

**Architecture:** Apply Admin authorization directly to discovery and install-job lookup endpoints. Keep mod listing readable, but construct `ModListResponse` with `ModsFolder = null` unless the authenticated role is Admin. Add runtime and static contracts for both behaviors.

**Tech Stack:** .NET 8, ASP.NET Core minimal APIs, xUnit integration tests, GitHub Actions validation scripts.

**Spec:** `docs/superpowers/specs/2026-11-03-sec-n04-readonly-host-path-disclosure.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Preserve ReadOnly monitoring/status/history and safe mod metadata.
- Preserve Admin discovery, install progress and full mod-folder diagnostics.
- Do not expose or log any token/password/secret values.
- Do not change write authorization semantics.

## Review Focus

- Authorization must run before nonexistent install-job lookup, producing 403 for ReadOnly rather than 404.
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

### Task 2: Enforce minimal endpoint exposure

**Files:**
- Modify: `src/GameServerControl.Agent/Program.cs`

**Interfaces:**
- `GET /api/discover` and `GET /api/servers/install/{jobId}` gain `RequireAuthorization("admin")`.
- `GET /api/servers/{id}/mods` accepts `HttpContext` and exposes `ModsFolder` only when role claim is Admin.

- [ ] **Step 1: Add Admin policy to discovery and install-job GETs**

Keep handlers unchanged; append the existing `admin` policy at endpoint mapping.

- [ ] **Step 2: Redact ReadOnly mod folder**

When constructing `ModListResponse`, pass `mgr.ModsFolder(def)` only for Admin role; otherwise pass null.

- [ ] **Step 3: Run integration tests and verify GREEN**

Expected: full integration project passes with CA1416 promoted to error.

### Task 3: Pin the security contract and verify CI

**Files:**
- Modify: `scripts/validate-security-access.mjs`

**Interfaces:**
- Static validator requires Admin protection on discovery/install-job lookup and role-aware mod-folder exposure.

- [ ] **Step 1: Extend the static security contract**

Require the two GET mappings to use the Admin policy and reject an unconditional `mgr.ModsFolder(def)` in the mod-list response.

- [ ] **Step 2: Run all PR workflows**

Require Build, Runtime integration tests, Version contract, Supply-chain audit contract, PERF contract, ARCH-01 and BUG-02 to succeed before merge.

- [ ] **Step 3: Review final diff**

Confirm changes are limited to endpoint exposure/redaction, security tests/contract and SEC-N04 documentation.
