# PERF-N02 Token Lookup Hashing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make managed-token lookup hash the presented credential once and eliminate allocations that scale with managed-token count.

**Architecture:** Keep `TokenRegistry` and its persisted schema unchanged. `Lookup` computes one normalized SHA-256 hex value for the presented credential, then compares it to each normalized stored hash in fixed time over allocation-free UTF-16 byte spans.

**Tech Stack:** .NET 8, ASP.NET Core, xUnit, `System.Security.Cryptography`, `System.Runtime.InteropServices`.

**Spec:** `docs/superpowers/specs/2026-11-03-perf-n02-token-lookup-hash.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Preserve legacy `Agent:ApiToken` behavior and all managed-token auth semantics.
- Preserve `tokens.json` format and migration behavior.
- Keep secret comparisons constant-time.
- Do not add dependencies or broaden this change into token-count limits or indexing.

## Review Focus

- Managed-token hit still returns the correct id/name/role.
- Managed-token miss remains null.
- Legacy-token hit is unchanged.
- Large managed-token registries do not cause lookup allocations to scale with token count.
- Invalid stored hashes remain rejected by reload validation and cannot break lookup.

---

### Task 1: Remove per-token hashing and allocations

**Files:**
- Create: `tests/GameServerControl.Agent.IntegrationTests/TokenRegistryPerformanceTests.cs`
- Modify: `src/GameServerControl.Agent/Auth/TokenRegistry.cs`

**Interfaces:**
- Consumes: `TokenRegistry.Lookup(string)` and persisted normalized `TokenHash` hex strings.
- Produces: the same `Lookup` results with allocation cost independent of managed-token count.

- [ ] **Step 1: Write the failing allocation regression**

Create two temporary registries containing 1 and 128 valid managed-token records. Warm both registries, measure allocations for repeated missing-token `Lookup` calls with `GC.GetAllocatedBytesForCurrentThread`, and assert the 128-token registry stays within a fixed 32 KiB delta of the one-token registry.

- [ ] **Step 2: Run runtime integration tests and verify RED**

Run: `dotnet test tests/GameServerControl.Agent.IntegrationTests/GameServerControl.Agent.IntegrationTests.csproj -c Release --filter TokenRegistryPerformanceTests`

Expected: FAIL because current `HashMatches` re-hashes the presented token and allocates a decoded stored hash for every managed token.

- [ ] **Step 3: Implement the minimal optimization**

In `TokenRegistry.Lookup`, call `HashToken(presented)` once after the legacy-token check. Change `HashMatches` to accept that normalized presented hash and compare its fixed-length UTF-16 bytes to the normalized stored hash through `MemoryMarshal.AsBytes` and `CryptographicOperations.FixedTimeEquals`, with no per-token allocations.

Ruling: the originally planned span decode via `Convert.FromHexString` is not available as an allocation-free destination-span API on the repository's .NET 8 target; fixed-time comparison of the already-normalized in-memory hex representation preserves the security property and allocation goal without changing target framework or storage format.

- [ ] **Step 4: Verify GREEN and regression behavior**

Run the focused test, the full integration project, and require every PR workflow to pass.

Expected: allocation regression passes, existing authentication/token behavior remains green, and Build/runtime workflows succeed on Linux and Windows.

- [ ] **Step 5: Review and merge**

Review the final diff for persisted-format/auth-semantic changes. Squash-merge only after all PR workflows are green.
