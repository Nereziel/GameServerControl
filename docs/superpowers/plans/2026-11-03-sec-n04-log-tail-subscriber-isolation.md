# SEC-N04 Log Tail Subscriber Isolation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make LogOnly live-tail lifecycle connection-scoped so one ReadOnly client cannot stop another client's live logs.

**Architecture:** `StatusHub` becomes the automatic lifecycle boundary for LogOnly streams. `LogTailService` tracks per-server SignalR subscribers and manual Admin ownership under one synchronization gate, starts one tail for the first owner, and stops subscriber-owned tails only after the last subscriber disconnects. Existing ReadOnly HTTP tail calls stay compatible but become side-effect free; Admin HTTP start/stop keeps global control semantics.

**Tech Stack:** .NET 8, ASP.NET Core SignalR/minimal APIs, xUnit integration tests.

**Spec:** `docs/superpowers/specs/2026-11-03-sec-n04-log-tail-subscriber-isolation.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Preserve Admin HTTP `/api/servers/{id}/logs/tail` start/stop compatibility.
- Preserve existing ReadOnly HTTP compatibility without allowing ReadOnly callers to mutate global log-tail lifecycle.
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

### Task 1: Prove cross-client lifecycle weakness

**Files:**
- Modify: `tests/GameServerControl.Agent.IntegrationTests/SignalRIsolationIntegrationTests.cs`

**Interfaces:**
- Produces: multi-client lifecycle and ReadOnly-stop isolation regressions.

- [x] **Step 1: Add a failing SignalR lifecycle test**

Create two independent ReadOnly SignalR connections subscribed to LogOnly `alpha`, without HTTP ownership. Verify both receive a first appended sentinel, disconnect the first, then verify the second receives a second appended sentinel. Finally disconnect the second and verify `LogTailService.IsTailing("alpha")` becomes false.

- [x] **Step 2: Run runtime integration tests and verify RED**

Run: `dotnet test tests/GameServerControl.Agent.IntegrationTests/GameServerControl.Agent.IntegrationTests.csproj -c Release -p:WarningsAsErrors=CA1416`

Observed RED on Ubuntu: the lifecycle test timed out waiting for `tail.IsTailing("alpha")` because SignalR subscription did not start the tail. The initial authorization-design test also showed the existing ReadOnly compatibility exception returned 200; that 403 design was superseded by the compatibility ruling in the spec.

- [ ] **Step 3: Add a ReadOnly HTTP stop isolation regression before changing endpoint behavior**

With an active LogOnly SignalR subscriber-owned tail, POST `{"action":"stop"}` using a ReadOnly token. Require HTTP success for compatibility, require `IsTailing("alpha")` to remain true, and require a subsequent live sentinel to reach the connected subscriber. Current endpoint behavior must fail because it invokes global `Stop`.

### Task 2: Move automatic lifecycle into SignalR subscriptions

**Files:**
- Modify: `src/GameServerControl.Agent/Logs/LogTailService.cs`
- Modify: `src/GameServerControl.Agent/Hubs/StatusHub.cs`

**Interfaces:**
- Produces: `Subscribe(string serverId, string connectionId) -> bool` and `UnsubscribeConnection(string connectionId) -> void` behavior on `LogTailService`.

- [x] **Step 1: Add connection-scoped subscriber state**

Track per-server subscriber IDs plus manual Admin ownership under one lock. `Subscribe` validates tail eligibility, deduplicates the connection/server pair, and ensures the one tail loop exists. `UnsubscribeConnection` removes the connection from every subscribed server and cancels a tail only when that server has no remaining subscribers and no manual Admin owner.

- [x] **Step 2: Preserve manual Admin semantics**

`Start` marks manual ownership and ensures the tail exists. `Stop` remains a forceful global Admin override. `IsTailing` continues to report actual active loop state. Do not replay backlog from lifecycle methods.

- [x] **Step 3: Wire the hub**

For `GameConsoleMode.LogOnly`, call `_logTail.Subscribe(serverId, Context.ConnectionId)` before caller-only backlog delivery. In `OnDisconnectedAsync`, call `_logTail.UnsubscribeConnection(Context.ConnectionId)` as well as the existing stdin unsubscribe.

### Task 3: Make ReadOnly HTTP compatibility side-effect free

**Files:**
- Modify: `src/GameServerControl.Agent/Program.cs`
- Modify: `tests/GameServerControl.Agent.IntegrationTests/SignalRIsolationIntegrationTests.cs`
- Restore unchanged: `src/GameServerControl.Agent/Auth/RoleEnforcementMiddleware.cs`
- Restore unchanged: `tests/GameServerControl.Agent.IntegrationTests/RoleEnforcementMiddlewareTests.cs`

**Interfaces:**
- Consumes: Task 2 SignalR-owned lifecycle.

- [ ] **Step 1: Restore the existing ReadOnly control-plane routing exception**

Keep `RoleEnforcementMiddleware` behavior compatible. The endpoint, not middleware, will distinguish Admin mutation from ReadOnly side-effect-free compatibility calls.

- [ ] **Step 2: Make `/logs/tail` role-aware**

Validate `action` as today. If caller role is not Admin, return current `{ tailing = ... }` state without invoking `Start` or `Stop`. If caller is Admin, retain existing `start`/`stop` behavior exactly.

- [ ] **Step 3: Update existing explicit lifecycle tests to use Admin HTTP tokens**

`Log_tail_reaches_only_the_subscribed_server_group` and `Repeated_log_tail_start_does_not_replay_recent_backlog` exercise explicit/manual HTTP lifecycle and must use `AgentTestFactory.AdminToken` for their HTTP clients.

- [ ] **Step 4: Run full integration suite and verify GREEN**

Expected: lifecycle isolation, ReadOnly stop no-op, backlog isolation, group isolation and repeated Admin start all pass on Ubuntu and Windows with `CA1416` promoted to an error.

- [ ] **Step 5: Run all PR workflows and review final diff**

Require Build, Runtime integration tests, Version contract, Supply-chain audit contract, PERF contract, ARCH-01 and BUG-02 to succeed before merge. Verify no Web UI change is needed, no auth policy outside LogTail changed, and no backlog/read bounds changed.
