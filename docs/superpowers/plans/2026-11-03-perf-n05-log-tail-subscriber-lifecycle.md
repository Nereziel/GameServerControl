# PERF-N05 Log Tail Subscriber Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make shared LogOnly tail lifetime follow active SignalR subscribers and make HTTP tail control Admin-only.

**Architecture:** `StatusHub` registers LogOnly connection/server subscriptions with `LogTailService` and removes all subscriptions for a connection on disconnect. `LogTailService` tracks subscribers per active handle, automatically stops a non-manual handle after its final subscriber leaves, and starts background work only for the handle that wins dictionary insertion. The existing HTTP endpoint remains a privileged manual control; Web UI no longer uses it for normal console lifecycle.

**Tech Stack:** .NET 8, ASP.NET Core minimal APIs, SignalR, xUnit integration tests, browser JavaScript.

**Spec:** `docs/superpowers/specs/2026-11-03-perf-n05-log-tail-subscriber-lifecycle.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Preserve one shared live tail per server.
- Preserve caller-only bounded recent backlog semantics.
- Preserve Admin HTTP start/stop compatibility and force-stop semantics.
- ReadOnly HTTP writes to `/logs/tail` must be forbidden.
- Preserve stdin/Runner console lifecycle and all existing log read/rotation bounds.

## Review Focus

- Two simultaneous LogOnly connections to one server retain one tail when either disconnects.
- Final subscriber disconnect cancels an automatically owned tail.
- Same connection subscribing to the same server twice is idempotent.
- One connection subscribing to multiple LogOnly servers is fully cleaned up on disconnect.
- Concurrent starts/subscribes cannot launch orphan tail loops from losing `GetOrAdd` factories.

---

### Task 1: Prove authorization and multi-client lifecycle failures

**Files:**
- Modify: `tests/GameServerControl.Agent.IntegrationTests/RoleEnforcementMiddlewareTests.cs`
- Modify: `tests/GameServerControl.Agent.IntegrationTests/SignalRIsolationIntegrationTests.cs`

**Interfaces:**
- Produces: regression contracts for ReadOnly HTTP denial and subscriber-owned LogOnly lifetime.

- [ ] **Step 1: Write the failing ReadOnly control test**

Add a direct middleware test for `POST /api/servers/alpha/logs/tail`, asserting 403 and that the next delegate is not called.

- [ ] **Step 2: Write the failing two-subscriber lifecycle integration test**

Use `AgentTestFactory(alphaUsesLogProfile: true)`. Connect two ReadOnly SignalR clients, subscribe both to `alpha`, and use an Admin HTTP client only to query `GET /api/servers/alpha/logs/tail`. Require tailing=true after subscription, tailing=true after the first disconnect, delivery of an appended sentinel to the remaining client, and tailing=false after the final disconnect.

- [ ] **Step 3: Run runtime integration and verify RED**

Run: `dotnet test tests/GameServerControl.Agent.IntegrationTests/GameServerControl.Agent.IntegrationTests.csproj -c Release -p:WarningsAsErrors=CA1416`

Expected: the ReadOnly POST test reports 200/next-called instead of 403, and the subscriber lifecycle test cannot observe automatic tail startup.

### Task 2: Make tail lifecycle connection-aware

**Files:**
- Modify: `src/GameServerControl.Agent/Logs/LogTailService.cs`
- Modify: `src/GameServerControl.Agent/Hubs/StatusHub.cs`

**Interfaces:**
- Produces: `Subscribe(string serverId, string connectionId) -> bool` and `UnsubscribeConnection(string connectionId) -> void` on `LogTailService`.
- Keeps: `Start(string serverId) -> bool`, `Stop(string serverId) -> bool`, `GetRecentAsync(...)`.

- [ ] **Step 1: Refactor handle creation so only the dictionary winner starts `TailLoop`**

Do not start `Task.Run` inside the `GetOrAdd` value factory. A newly constructed candidate may start only when `ReferenceEquals(candidate, returnedHandle)`.

- [ ] **Step 2: Add idempotent per-handle subscriber tracking**

Register connection IDs under a handle gate. `Subscribe` ensures a valid BareMetal log tail exists and records the connection once. `UnsubscribeConnection` removes that ID from every active handle and cancels/removes an automatically managed handle only when it has no subscribers and is not manually started.

- [ ] **Step 3: Preserve Admin manual control semantics**

`Start` marks the active handle as manually started. `Stop` force-removes and cancels the active handle regardless of subscribers.

- [ ] **Step 4: Wire SignalR lifecycle**

For `GameConsoleMode.LogOnly`, `StatusHub.SubscribeConsole` must register the connection before sending backlog. `OnDisconnectedAsync` must remove both stdin and LogOnly connection subscriptions.

- [ ] **Step 5: Run integration tests and verify GREEN**

Run the full integration project with CA1416 promoted to error. Expected: all tests pass on the local/CI platform.

### Task 3: Remove ReadOnly HTTP control and Web UI start/stop coupling

**Files:**
- Modify: `src/GameServerControl.Agent/Auth/RoleEnforcementMiddleware.cs`
- Modify: `src/GameServerControl.Agent/WebUi/server-workspace.js`
- Modify: `tests/GameServerControl.Agent.IntegrationTests/SignalRIsolationIntegrationTests.cs`

**Interfaces:**
- HTTP `POST /api/servers/{id}/logs/tail` remains available under normal Admin write authorization.
- Web UI LogOnly console lifecycle uses only SignalR connect/subscribe/disconnect.

- [ ] **Step 1: Remove the ReadOnly `/logs/tail` POST exception**

Delete the special-case suffix/parser and let all API writes follow the normal Admin rule.

- [ ] **Step 2: Remove Web UI HTTP start/stop calls**

After `SubscribeConsole`, store the SignalR connection directly for LogOnly mode just like other console modes; closing it triggers hub disconnect cleanup.

- [ ] **Step 3: Update existing manual-start regression to use Admin credentials**

Keep `Repeated_log_tail_start_does_not_replay_recent_backlog` as an Admin manual-endpoint compatibility test, because ReadOnly HTTP control is no longer valid.

- [ ] **Step 4: Run full runtime integration and Web UI validation**

Expected: integration tests pass and `node scripts/validate-webui.mjs` passes.

- [ ] **Step 5: Run all PR workflows and review diff**

Require Build, Runtime integration tests, Version contract, Supply-chain audit contract, PERF contract, ARCH-01, BUG-02, and any current platform-analyzer gate to succeed before merge. Review that changes are limited to tail lifecycle, role enforcement, Web UI coupling, tests and PERF-N05 docs.
