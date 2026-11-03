# QA-N01 Agent Platform Analyzer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Agent build free of the known `CA1416` platform warning and lock that requirement into CI without suppressing analyzers.

**Architecture:** Promote `CA1416` to an error for the Agent build in CI, then give `SecretProtector` a local non-Windows guard in the Unix key-creation method so analyzer flow analysis proves `UnixCreateMode` is unreachable on Windows. Runtime cryptography and persisted formats stay unchanged.

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
- CI contract targets Agent `CA1416` specifically rather than globally converting unrelated warnings to errors.

---

### Task 1: Add the failing analyzer contract

**Files:**
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- Produces: Agent build commands that promote `CA1416` to an error.

- [ ] **Step 1: Change the Agent build validation to pass `-warnaserror:CA1416` on Windows and Linux without changing unrelated warning policy.**

- [ ] **Step 2: Run PR CI and verify RED.**

Expected: Agent build fails at `SecretProtector.cs` on `FileStreamOptions.UnixCreateMode` with `CA1416`.

### Task 2: Make Unix key creation locally platform-safe

**Files:**
- Modify: `src/GameServerControl.Agent/Security/SecretProtector.cs`

**Interfaces:**
- Consumes: Task 1 CI contract.
- Produces: analyzer-visible non-Windows guard in `GetOrCreateUnixKey()`.

- [ ] **Step 1: Add an explicit Windows rejection at the start of `GetOrCreateUnixKey()` before any Unix-only API use.**

Use `PlatformNotSupportedException` because this private method represents a Unix-only operation. Do not add pragma or attributes solely to suppress analysis.

- [ ] **Step 2: Run PR CI and verify GREEN.**

Expected: Agent build passes with `CA1416` promoted to an error; SEC-08 checks remain green on Windows and Linux.

- [ ] **Step 3: Review the final diff.**

Expected: only docs, targeted CI analyzer contract, and one local platform guard change.
