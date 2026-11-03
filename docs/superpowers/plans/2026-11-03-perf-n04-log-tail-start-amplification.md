# PERF-N04 Log Tail Start Amplification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make repeated log-tail `start` requests side-effect free while preserving one bounded backlog delivery per legitimate `LogOnly` SignalR subscriber.

**Architecture:** Keep `LogTailService` responsible for the one-per-server live tail and bounded recent-file reading, but stop broadcasting recent history from `Start`. `StatusHub.SubscribeConsole` will deliver recent lines to `Clients.Caller` once per connection/server, matching the existing caller-only stdin initial-state pattern.

**Tech Stack:** .NET 8, ASP.NET Core minimal APIs, SignalR, xUnit integration tests.

**Spec:** `docs/superpowers/specs/2026-11-03-perf-n04-log-tail-start-amplification.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Keep HTTP `POST /api/servers/{id}/logs/tail` start/stop compatibility.
- Keep one live tail loop per server.
- Keep recent history bounded to 256 KiB and 100 lines for the UI path.
- Do not change auth/role/path semantics.
- Preserve live `logLine` group broadcasts and rotation behavior.

## Review Focus

- Same connection calling `SubscribeConsole` twice must not replay recent history twice.
- A second connection to the same `LogOnly` server must still get its own recent history.
- Repeated HTTP `start` must not trigger recent-history reads or group broadcasts.
- Live appended lines must still reach the subscribed server group after start.
- Empty/missing log files must keep returning a usable console state without errors.

---

### Task 1: Prove repeated start replay amplification

**Files:**
- Modify: `tests/GameServerControl.Agent.IntegrationTests/AgentTestFactory.cs`
- Modify: `tests/GameServerControl.Agent.IntegrationTests/SignalRIsolationIntegrationTests.cs`

**Interfaces:**
- Produces: a test fixture option for an `alpha` LogOnly profile and a regression test exercising the real hub plus HTTP endpoint.

- [ ] **Step 1: Write the failing integration test**

Add a LogOnly alpha fixture. Prepopulate its log, connect a ReadOnly SignalR client, call `SubscribeConsole`, then call HTTP `start` once and wait until the history line is observed. Record its occurrence count, call HTTP `start` again, and assert the count does not increase.

- [ ] **Step 2: Run runtime integration tests and verify RED**

Run: `dotnet test tests/GameServerControl.Agent.IntegrationTests/GameServerControl.Agent.IntegrationTests.csproj -c Release`

Expected: the new test fails because the second `start` schedules another `EmitRecentAsync` and rebroadcasts the same history.

### Task 2: Separate caller backlog from live-tail start

**Files:**
- Modify: `src/GameServerControl.Agent/Logs/LogTailService.cs`
- Modify: `src/GameServerControl.Agent/Hubs/StatusHub.cs`
- Modify: `tests/GameServerControl.Agent.IntegrationTests/SignalRIsolationIntegrationTests.cs`

**Interfaces:**
- Produces: `LogTailService.GetRecentAsync(string serverId, int maxLines = 100, CancellationToken ct = default) -> Task<LogLine[]>`.

- [ ] **Step 1: Remove recent-history replay from `LogTailService.Start`**

`Start` should only create/reuse the one live tail handle and return its active state.

- [ ] **Step 2: Expose bounded recent history without broadcasting**

Refactor existing recent-file logic into `GetRecentAsync`; keep the 256 KiB read cap and line clamp. Return the existing recent-history marker followed by bounded `LogLine` values.

- [ ] **Step 3: Deliver LogOnly backlog from `StatusHub.SubscribeConsole`**

For `GameConsoleMode.LogOnly`, send `GetRecentAsync` results through `Clients.Caller`. Use `Context.Items` with a per-server key so the same connection cannot replay the backlog by invoking `SubscribeConsole` repeatedly.

- [ ] **Step 4: Verify GREEN and multi-client behavior**

Run the full integration project. Add/retain assertions showing a second connection still receives its own backlog while repeated `start` and repeated same-connection subscription do not replay it.

- [ ] **Step 5: Run all PR workflows and review diff**

Require Build, Runtime integration tests, Version contract, Supply-chain audit contract, PERF contract, ARCH-01 and BUG-02 to succeed before merge. Review that only log-tail backlog delivery, tests and docs changed.
