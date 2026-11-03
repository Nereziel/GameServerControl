# PERF-N03 Runner Console Generation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect Runner process restarts independently of sequence values so the Agent cannot skip early console lines from a new Runner generation.

**Architecture:** Extend the shared Runner console cursor with nullable generation metadata. The Runner owns a generation ID for its process lifetime and returns full bounded history on generation mismatch; the Agent carries that generation in poll requests and resets its local sequence cursor when the snapshot generation changes. Nullable fields preserve additive JSON compatibility with older Agent/Runner versions.

**Tech Stack:** .NET 8, ASP.NET Core SignalR, named-pipe JSON protocol, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-03-perf-n03-runner-generation.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Keep `RunnerProtocolLimits` unchanged.
- Keep generation fields nullable for version-skew compatibility.
- Preserve the existing sequence-reset fallback for generation-less Runner responses.
- Do not persist generation or sequence state to disk.
- Do not change unrelated Runner lifecycle or provisioning behavior.

## Review Focus

- New Runner generation with `LastSequence >= old cursor` must return and broadcast early lines starting at sequence 1.
- Same-generation polling must remain delta-only.
- Old Runner snapshots with `Generation = null` must keep sequence-reset fallback behavior.
- Initial subscription must not rebroadcast its initial history as a same-generation delta.
- Request validation must reject generation metadata on non-`ConsoleState` commands and unreasonably large generation values.

---

### Task 1: Extend the Runner protocol and host resync semantics

**Files:**
- Modify: `src/GameServerControl.Shared/RunnerProtocol.cs`
- Modify: `src/GameServerControl.Runner/RunnerProcessHost.cs`
- Modify: `src/GameServerControl.Runner/RunnerPipeServer.cs`
- Modify: `tests/GameServerControl.Agent.IntegrationTests/RunnerProtocolTests.cs`
- Modify: `tests/GameServerControl.Agent.IntegrationTests/RunnerConsoleDeltaTests.cs`

**Interfaces:**
- Produces: `RunnerRequest.ConsoleGeneration : string?` and `RunnerConsoleSnapshot.Generation : string?`.
- Produces: `RunnerProcessHost.ConsoleState(long? afterSequence = null, string? consoleGeneration = null)`.

- [ ] **Step 1: Write failing protocol and Runner restart regressions**

Add a protocol test requiring `ConsoleGeneration` on `RunnerRequest`, validation that it is console-state-only and bounded, and a Windows Runner test with two `RunnerProcessHost` instances where the second instance reaches sequence 5 while the old cursor is 3. Poll the second host with the first host's generation and assert sequences 1..5 are returned.

- [ ] **Step 2: Run focused tests and verify RED**

Run the integration test project with filters for `RunnerProtocolTests` and `RunnerConsoleDeltaTests` on the applicable platform.

Expected: FAIL because generation metadata and mismatch resync do not exist.

- [ ] **Step 3: Implement additive protocol metadata and Runner-side generation mismatch resync**

Append nullable generation fields to shared records; create a per-host 32-character generation ID; pass request generation through `RunnerPipeServer`; return full bounded history when a non-null request generation differs from the host generation.

- [ ] **Step 4: Verify Task 1 GREEN**

Run the focused tests again.

Expected: protocol validation and Runner generation mismatch tests pass while existing same-generation/sequence-reset delta behavior remains green.

### Task 2: Make the Agent pump generation-aware

**Files:**
- Modify: `src/GameServerControl.Agent/Hyperv/IWindowsRunnerServiceManager.cs`
- Modify: `src/GameServerControl.Agent/Hyperv/WindowsRunnerServiceManager.cs`
- Modify: `src/GameServerControl.Agent/Hyperv/LocalProcessService.cs`
- Modify: `tests/GameServerControl.Agent.IntegrationTests/FakeWindowsRunnerServiceManager.cs`
- Create or modify: `tests/GameServerControl.Agent.IntegrationTests/SignalRIsolationIntegrationTests.cs` (or a focused console-pump regression file following existing integration patterns)

**Interfaces:**
- Consumes: Task 1 `RunnerRequest.ConsoleGeneration` and `RunnerConsoleSnapshot.Generation`.
- Produces: `PollConsoleStateAsync(ServerDef def, string? consoleGeneration, long afterSequence, CancellationToken ct = default)`.

- [ ] **Step 1: Write failing Agent pump regression**

Use the fake Runner manager and a SignalR console subscription on Windows. Return initial generation `old` at sequence 3, then a poll snapshot for generation `new` with sequences 1..5 and `LastSequence = 5`. Assert new-generation lines 1..5 are broadcast instead of only 4..5.

- [ ] **Step 2: Run runtime integration tests and verify RED**

Expected: FAIL because the pump only resets when `LastSequence < lastSequence` and does not track generation.

- [ ] **Step 3: Thread generation through initial state, pump state, manager polling and fake manager**

Track `(Generation, LastSequence)` from the initial Runner snapshot. Send both on each poll. When both old and new generation values are non-null and differ, reset `lastSequence = 0` before filtering lines; then adopt the new generation. Preserve the existing sequence fallback for null generations.

- [ ] **Step 4: Verify full suite and CI**

Run the full integration project and require all PR workflows to succeed on Linux and Windows.

Expected: all tests pass; Build, Runtime integration, Version, Supply-chain, PERF, ARCH and BUG contracts are green.

- [ ] **Step 5: Review and merge**

Review the final diff for additive protocol compatibility and bounded-history behavior. Squash-merge only after all workflows are green.