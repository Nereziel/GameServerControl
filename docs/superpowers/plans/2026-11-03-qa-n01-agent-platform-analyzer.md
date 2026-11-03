# QA-N01 Agent Platform Analyzer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Agent build free of the known `CA1416` platform warning and lock that requirement into CI without suppressing analyzers.

**Architecture:** Promote `CA1416` to an error in the existing Ubuntu/Windows runtime-integration build path, where the warning is reproducible while the Agent is built as a test dependency. Then give `SecretProtector` a local non-Windows guard in the Unix key-creation method so analyzer flow analysis proves `UnixCreateMode` is unreachable on Windows. Runtime cryptography and persisted formats stay unchanged.

**Tech Stack:** .NET 8, .NET platform compatibility analyzer, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-03-qa-n01-agent-platform-analyzer.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Do not suppress `CA1416`.
- Do not change encrypted envelope formats, key size, file mode, DPAPI scope, AES-GCM parameters, or race handling.
- Keep SEC-08 checks green on Windows and Linux.

## Review Focus

- Windows DPAPI path still avoids Unix file APIs.
- Unix key file still uses mode 0600 at creation.
- Existing Unix key creation race still falls back to reading the winner's key.
- Missing-key recovery behavior remains unchanged.
- CI contract targets `CA1416` specifically rather than globally converting unrelated warnings to errors.

---

### Task 1: Add the failing analyzer contract

**Files:**
- Modify: `.github/workflows/runtime-integration.yml`

**Interfaces:**
- Produces: Ubuntu and Windows runtime-integration builds that promote `CA1416` to an error while building the Agent dependency.

- [x] **Step 1: Change the runtime integration command to pass `-p:WarningsAsErrors=CA1416` without changing unrelated warning policy.**

- [x] **Step 2: Run PR CI and verify RED.**

Observed RED on Ubuntu: `SecretProtector.cs(153,21): error CA1416` for `FileStreamOptions.UnixCreateMode.set`; process exited 1 before tests ran.

Ruling: the first attempted direct Agent-build contract was discarded because it did not reproduce the warning reliably. The runtime-integration matrix is the binding regression contract because that existing build path reproducibly emitted the warning before this change.

### Task 2: Make Unix key creation locally platform-safe

**Files:**
- Modify: `src/GameServerControl.Agent/Security/SecretProtector.cs`

**Interfaces:**
- Consumes: Task 1 runtime-integration analyzer contract.
- Produces: analyzer-visible non-Windows guard in `GetOrCreateUnixKey()`.

- [ ] **Step 1: Add an explicit Windows rejection at the start of `GetOrCreateUnixKey()` before any Unix-only API use.**

Use `PlatformNotSupportedException` because this private method represents a Unix-only operation. Do not add pragma or attributes solely to suppress analysis.

- [ ] **Step 2: Run PR CI and verify GREEN.**

Expected: runtime integration builds and tests pass with `CA1416` promoted to an error; SEC-08 checks remain green on Windows and Linux.

- [ ] **Step 3: Review the final diff.**

Expected: only docs, targeted runtime-integration analyzer contract, and one local platform guard change.
