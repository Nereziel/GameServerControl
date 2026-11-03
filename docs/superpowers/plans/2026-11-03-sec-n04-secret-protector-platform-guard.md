# SEC-N04 SecretProtector Platform Guard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the SecretProtector CA1416 warning while preserving atomic Unix key permissions and adding a CI regression gate.

**Architecture:** Keep the existing platform split and secret formats. Make the Unix-only key creation method self-guarding, and make Agent builds fail specifically on future CA1416 diagnostics.

**Tech Stack:** .NET 8, C#, .NET platform compatibility analyzer, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-03-sec-n04-secret-protector-platform-guard.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Preserve Windows DPAPI behavior.
- Preserve Unix AES-GCM envelope and key format.
- Preserve atomic Unix key creation with mode `UserRead | UserWrite`.
- Treat only CA1416 as an added build error; do not globally enable warnings-as-errors.

## Review Focus

- Linux first-run key creation still creates the same key file with private permissions.
- Existing Unix key reads remain unchanged.
- Windows DPAPI path never reaches Unix key creation.
- An accidental future call to Unix key creation on Windows fails before touching `UnixCreateMode`.
- CI does not convert unrelated warnings to failures.

---

### Task 1: Add the CA1416 regression gate

**Files:**
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- Produces: Agent build commands that pass `-p:WarningsAsErrors=CA1416` on Windows and Linux.

- [ ] **Step 1: Add the failing build contract**

Update the Windows `Build (Release)` and Linux `Build Agent (Release)` commands so CA1416 is promoted to an error and no other warning code is promoted.

- [ ] **Step 2: Run PR Build workflow and verify RED**

Expected: Agent compilation fails at `SecretProtector.cs` on `FileStreamOptions.UnixCreateMode` with CA1416 promoted to an error.

### Task 2: Guard Unix-only key creation

**Files:**
- Modify: `src/GameServerControl.Agent/Security/SecretProtector.cs`

**Interfaces:**
- `GetOrCreateUnixKey() -> byte[]` remains private and unchanged in signature.

- [ ] **Step 1: Implement the minimal platform guard**

At the start of `GetOrCreateUnixKey`, add an explicit Windows rejection before the lock and before constructing `FileStreamOptions` with `UnixCreateMode`. Do not remove or defer `UnixCreateMode`.

- [ ] **Step 2: Verify GREEN**

Require Build to pass on Windows and Linux with CA1416-as-error still enabled.

- [ ] **Step 3: Run the full PR workflow set**

Require Build, Runtime integration tests, Version contract, Supply-chain audit contract, PERF contract, ARCH-01 and BUG-02 to succeed before merge.

- [ ] **Step 4: Review final diff**

Confirm only the targeted platform guard, CA1416 CI gate, and SEC-N04 documentation changed.
