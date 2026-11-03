# PERF-N01 Runner Console Polling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Windows Runner console polling incremental and subscriber-scoped so historical console data is not retransmitted every 250 ms and polling ends after the last browser disconnects.

**Architecture:** Extend the existing Runner request/response protocol with an optional console sequence cursor, then make `LocalProcessService` own one subscriber-aware pump per server. `StatusHub` explicitly registers a console subscriber and unregisters all subscriptions on connection disconnect.

**Tech Stack:** .NET 8, ASP.NET Core SignalR, Windows named pipes, xUnit, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-03-perf-n01-runner-console-design.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Keep one Runner pump per server, not per browser connection.
- Preserve initial bounded history and current protocol size limits.
- Do not change Linux/local console behavior.
- Do not replace named-pipe request/response with streaming transport.

## Review Focus

- Negative or non-console cursors must be rejected.
- A current cursor with no new output must return an empty line array but the current `LastSequence`.
- Duplicate subscription by one connection must not multiply pumps.
- One connection subscribed to multiple servers must be removed from all of them on disconnect.
- A cancelled old pump must not remove or dispose a newly created pump for the same server.

---

### Task 1: Add incremental Runner console protocol

**Files:**
- Modify: `src/GameServerControl.Shared/RunnerProtocol.cs`
- Modify: `src/GameServerControl.Runner/RunnerProcessHost.cs`
- Modify: `src/GameServerControl.Runner/RunnerPipeServer.cs`
- Modify: `src/GameServerControl.Agent/Hyperv/IWindowsRunnerServiceManager.cs`
- Modify: `src/GameServerControl.Agent/Hyperv/WindowsRunnerServiceManager.cs`
- Modify: `tests/GameServerControl.Agent.IntegrationTests/RunnerProtocolTests.cs`
- Modify: `tools/GameServerControl.RunnerCheck/Program.cs`
- Modify: `tools/GameServerControl.RunnerCheck/GameServerControl.RunnerCheck.csproj`

**Interfaces:**
- Produces: `RunnerRequest.AfterSequence : long?`, `RunnerProcessHost.ConsoleState(long? afterSequence = null)`, and `PollConsoleStateAsync(ServerDef, long afterSequence, CancellationToken)`.

- [ ] **Step 1: Write failing protocol/Runner delta tests**
- [ ] **Step 2: Run CI and verify RED for the missing cursor/delta behavior**
- [ ] **Step 3: Implement the optional command-scoped cursor and delta snapshot**
- [ ] **Step 4: Update Agent Runner polling to send the cursor and ARCH-01 contract to allow only this new bounded field**
- [ ] **Step 5: Verify protocol/Runner checks GREEN**

### Task 2: Tie the single server pump to active SignalR subscribers

**Files:**
- Modify: `src/GameServerControl.Agent/Hyperv/LocalProcessService.cs`
- Modify: `src/GameServerControl.Agent/Hubs/StatusHub.cs`
- Modify: `tests/GameServerControl.Agent.IntegrationTests/FakeWindowsRunnerServiceManager.cs`
- Modify: `tests/GameServerControl.Agent.IntegrationTests/SignalRIsolationIntegrationTests.cs`

**Interfaces:**
- Consumes: cursor-aware `PollConsoleStateAsync` from Task 1.
- Produces: explicit Windows console subscription registration and connection-wide unsubscribe cleanup.

- [ ] **Step 1: Add failing lifecycle test/contract for disconnect cleanup and single-pump ownership**
- [ ] **Step 2: Run the runtime suite and verify RED**
- [ ] **Step 3: Register the SignalR connection when Windows stdin initial state is obtained**
- [ ] **Step 4: Override `StatusHub.OnDisconnectedAsync` and cancel a server pump when its final subscriber is removed**
- [ ] **Step 5: Pass the current sequence cursor on every pump poll and make pump cleanup race-safe**
- [ ] **Step 6: Run full PR CI and require all workflows GREEN**
