# SEC-N04 Log Tail Subscriber Isolation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make LogOnly live-tail lifecycle connection-scoped so one ReadOnly client cannot stop another client's live logs.

**Architecture:** `StatusHub` becomes the automatic lifecycle boundary for LogOnly streams. `LogTailService` tracks per-server SignalR subscribers and manual Admin ownership under one synchronization gate, starts one tail for the first owner, and stops subscriber-owned tails only after the last subscriber disconnects. The ReadOnly HTTP mutation exception and Web UI HTTP tail controls are removed; Admin HTTP start/stop remains compatible.

**Tech Stack:** .NET 8, ASP.NET Core SignalR/minimal APIs, xUnit integration tests, browser JavaScript.

**Spec:** `docs/superpowers/specs/2026-11-03-sec-n04-log-tail-subscriber-isolation.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Preserve Admin HTTP `/api/servers/{id}/logs/tail` start/stop compatibility.
- ReadOnly tokens must not mutate global log-tail lifecycle.
- Preserve one live tail loop per server.
- Preserve the 256 KiB recent-read bound and 100-line hub backlog.
- Preserve existing live-line server-group isolation and rotation behavior.
- Preserve PERF-N04: repeated Admin `start` must not replay recent backlog.

## Review Focus

- Duplicate `SubscribeConsole` calls from one connection must not inflate subscriber count.
- Two independent connections to the same server must share one tail and survive either connection disconnecting first.
- A connection subscribed to multiple LogOnly servers must release each subscription on disconnect.
- Last-subscriber disconnect must not stop a manually Admin-started tail.
- Explicit Admin `stop` remains a deliberate global override even when subscribers exist.

---

### Task 1: Prove ReadOnly cross-client lifecycle weakness

**Files:**
- Modify: `tests/GameServerControl.Agent.IntegrationTests/SignalRIsolationIntegrationTests.cs`
- Modify: `tests/GameServerControl.Agent.IntegrationTests/RoleEnforcementMiddlewareTests.cs`

**Interfaces:**
- Produces: authorization and multi-client lifecycle regressions.

- [ ] **Step 1: Add a failing authorization test**

With a ReadOnly token and LogOnly alpha fixture, POST `{"action":"stop"}` to `/api/servers/alpha/logs/tail` and assert `403 Forbidden`.

- [ ] **Step 2: Add a failing SignalR lifecycle test**

Create two independent ReadOnly SignalR connections subscribed to LogOnly `alpha`, without HTTP start. Verify both receive a first appended sentinel, disconnect the first, then verify the second receives a second appended sentinel. Finally disconnect the second and verify `LogTailService.IsTailing("alpha")` becomes false.

- [ ] **Step 3: Run runtime integration tests and verify RED**

Run: `dotnet test tests/GameServerControl.Agent.IntegrationTests/GameServerControl.Agent.IntegrationTests.csproj -c Release -p:WarningsAsErrors=CA1416`

Expected: ReadOnly POST is accepted instead of 403 and/or LogOnly SignalR subscription does not start the live tail by itself.

### Task 2: Move automatic lifecycle into SignalR subscriptions

**Files:**
- Modify: `src/GameServerControl.Agent/Logs/LogTailService.cs`
- Modify: `src/GameServerControl.Agent/Hubs/StatusHub.cs`

**Interfaces:**
- Produces: `Subscribe(string serverId, string connectionId) -> bool` and `UnsubscribeConnection(string connectionId) -> void` behavior on `LogTailService`.

- [ ] **Step 1: Add connection-scoped subscriber state**

Track per-server subscriber IDs plus manual Admin ownership under one lock. `Subscribe` validates LogOnly-tail eligibility, deduplicates the connection/server pair, and ensures the one tail loop exists. `UnsubscribeConnection` removes the connection from every subscribed server and cancels a tail only when that server has no remaining subscribers and no manual Admin owner.

- [ ] **Step 2: Preserve manual Admin semantics**

`Start` marks manual ownership and ensures the tail exists. `Stop` remains a forceful global Admin override. `IsTailing` continues to report actual active loop state. Do not replay backlog from lifecycle methods.

- [ ] **Step 3: Wire the hub**

For `GameConsoleMode.LogOnly`, call `_logTail.Subscribe(serverId, Context.ConnectionId)` before/alongside caller-only backlog delivery. In `OnDisconnectedAsync`, call `_logTail.UnsubscribeConnection(Context.ConnectionId)` as well as the existing stdin unsubscribe.

- [ ] **Step 4: Run integration tests and verify lifecycle GREEN**

Expected: both new regressions pass and existing SignalR/PERF-N04 tests remain green.

### Task 3: Remove ReadOnly HTTP lifecycle control

**Files:**
- Modify: `src/GameServerControl.Agent/Auth/RoleEnforcementMiddleware.cs`
- Modify: `src/GameServerControl.Agent/WebUi/server-workspace.js`

**Interfaces:**
- Consumes: Task 2 SignalR-owned lifecycle.

- [ ] **Step 1: Remove the ReadOnly `/logs/tail` POST exception**

Delete `IsReadOnlyControlPost` and its special-case comment/constant so generic write enforcement returns 403 for ReadOnly POSTs.

- [ ] **Step 2: Remove Web UI HTTP start/stop calls**

After `SubscribeConsole`, store the SignalR connection directly for LogOnly mode as well. Closing the console closes the WebSocket; server disconnect cleanup owns tail release.

- [ ] **Step 3: Update existing PERF-N04 test to use an Admin token for explicit HTTP start/stop**

The repeated-start contract is about idempotent Admin/manual lifecycle and must remain covered without relying on the removed ReadOnly exception.

- [ ] **Step 4: Run full integration suite and all PR workflows**

Require Build, Runtime integration tests, Version contract, Supply-chain audit contract, PERF contract, ARCH-01 and BUG-02 to succeed before merge.

- [ ] **Step 5: Review final diff**

Verify no auth policy outside LogTail POST changed and no backlog/read bounds changed.
