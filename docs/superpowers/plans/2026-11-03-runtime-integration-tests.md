# Runtime Integration Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add runtime integration coverage for Agent authentication/roles/health, SignalR server isolation, Runner protocol behavior, and Runner deprovision/delete lifecycle.

**Architecture:** Use `WebApplicationFactory<Program>` against the real Agent entry point. Add only a `Testing` startup guard and an `IWindowsRunnerServiceManager` testability seam; replace that interface with an in-memory fake in integration tests while all HTTP middleware/endpoints and SignalR remain production wiring.

**Tech Stack:** .NET 8, ASP.NET Core `WebApplicationFactory`, TestServer, xUnit, SignalR .NET client.

**Spec:** `docs/superpowers/specs/2026-11-03-runtime-integration-tests-design.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Do not touch real Windows SCM, firewall, Scheduled Tasks or ACLs from integration tests.
- Test data lives under a unique temporary directory and is cleaned up.
- Production environments retain first-run token generation and the periodic status/relay loop unchanged.
- Existing ARCH/BUG/PERF/supply-chain contracts remain enabled.

## Review Focus

- `Testing` environment must not silently alter normal production startup.
- SignalR tests must prove a beta subscriber never receives an alpha sentinel, not merely that alpha receives it.
- ReadOnly authorization must be tested through the real middleware, including allowed GET and denied mutation.
- Delete failure must leave the server definition intact; successful delete must call deprovision before removal and completion after removal.
- The Runner request schema must remain incapable of carrying executable/path/argument launch metadata.

## Implementation status

The initial RED run compiled the Agent but failed the integration test project exactly because `Program` was not public and `IWindowsRunnerServiceManager` did not exist. The minimal entry-point/interface seams are now in place and compile; the next gate is the full runtime behavior suite on Windows and Linux.

---

### Task 1: Testable Agent entry point and Runner lifecycle interface

**Files:**
- Create: `src/GameServerControl.Agent/Hyperv/IWindowsRunnerServiceManager.cs`
- Modify: `src/GameServerControl.Agent/Hyperv/WindowsRunnerServiceManager.cs`
- Modify: `src/GameServerControl.Agent/Hyperv/LocalProcessService.cs`
- Modify: `src/GameServerControl.Agent/Program.cs`

**Interfaces:**
- Produces: `IWindowsRunnerServiceManager` with `Supports`, `StartAsync`, `FindAsync`, `StopAsync`, `ConsoleStateAsync`, `PollConsoleStateAsync`, `SendConsoleInputAsync`, `KillExtrasAsync`, `GetAutostartAsync`, `SetAutostartAsync`, `DeprovisionAsync`, and `CompleteDeprovision`.
- Produces: public partial `Program` for `WebApplicationFactory<Program>`.

- [x] **Step 1: Write the integration test project and compile-only tests that reference `Program` and substitute `IWindowsRunnerServiceManager`.**
- [x] **Step 2: Run the tests and verify RED because `Program` is not public and `IWindowsRunnerServiceManager` does not exist.**
- [x] **Step 3: Add the interface, map it to the existing singleton manager in DI, switch consumers to the interface, expose `PollConsoleStateAsync`, and add `public partial class Program`.**
- [x] **Step 4: Guard first-run token persistence and the periodic refresh loop with `!builder.Environment.IsEnvironment("Testing")`.**
- [ ] **Step 5: Run the tests and verify the host starts without production startup side effects.**

### Task 2: HTTP authentication, role and health integration tests

**Files:**
- Create: `tests/GameServerControl.Agent.IntegrationTests/GameServerControl.Agent.IntegrationTests.csproj`
- Create: `tests/GameServerControl.Agent.IntegrationTests/AgentTestFactory.cs`
- Create: `tests/GameServerControl.Agent.IntegrationTests/AuthenticationIntegrationTests.cs`
- Create: `tests/GameServerControl.Agent.IntegrationTests/TestCollection.cs`

**Interfaces:**
- `AgentTestFactory.CreateClientWithToken(string token)` returns a TestServer-backed `HttpClient`.
- `AgentTestFactory.CreateReadOnlyToken()` creates a managed ReadOnly token through the real `TokenRegistry`.

- [ ] **Step 1: Test unauthenticated `/api/health` -> 401 and Admin bearer `/api/health` -> 200 with `ok=true`.**
- [ ] **Step 2: Test `/api/health?access_token=<admin>` -> 401 while header authentication succeeds.**
- [ ] **Step 3: Test ReadOnly GET `/api/servers` -> 200 and does not expose seeded RCON/webhook plaintext.**
- [ ] **Step 4: Test ReadOnly POST `/api/servers/reload` -> 403 and Admin POST -> 200.**
- [ ] **Step 5: Run the test class and verify GREEN.**

### Task 3: Runtime SignalR server-group isolation

**Files:**
- Create: `tests/GameServerControl.Agent.IntegrationTests/SignalRIsolationIntegrationTests.cs`
- Modify: `tests/GameServerControl.Agent.IntegrationTests/AgentTestFactory.cs`

**Interfaces:**
- `AgentTestFactory.CreateHubConnection(string token)` returns a LongPolling TestServer `HubConnection`.
- Seeded servers `alpha` and `beta` have separate temporary log files.

- [ ] **Step 1: Connect two authenticated hub clients and call `SubscribeConsole("alpha")` / `SubscribeConsole("beta")`.**
- [ ] **Step 2: Start alpha log tail through `POST /api/servers/alpha/logs/tail`.**
- [ ] **Step 3: Append a unique sentinel to alpha's log and assert alpha receives that `logLine` within 5 seconds.**
- [ ] **Step 4: Keep beta listening through a bounded timeout and assert it never receives the alpha sentinel.**
- [ ] **Step 5: Stop the tail/connections in `finally` and verify the test is GREEN on TestServer LongPolling.**

### Task 4: Typed Runner protocol behavior tests

**Files:**
- Create: `tests/GameServerControl.Agent.IntegrationTests/RunnerProtocolTests.cs`

- [ ] **Step 1: Assert deterministic identity for `alpha`, distinct identity for `beta`, and no raw path separators/`..` from a hostile server ID.**
- [ ] **Step 2: Assert rooted Windows manifest paths validate while relative executable/working paths and path-valued stop process names fail.**
- [ ] **Step 3: Assert `RunnerRequest` public properties are exactly `Command`, `Force`, `ConsoleInput`; non-console commands reject `ConsoleInput`.**
- [ ] **Step 4: Assert console input accepts one bounded command and rejects blank, newline and >4096-character input.**
- [ ] **Step 5: Run the test class and verify GREEN.**

### Task 5: Runner deprovision/delete lifecycle integration tests

**Files:**
- Create: `tests/GameServerControl.Agent.IntegrationTests/FakeWindowsRunnerServiceManager.cs`
- Create: `tests/GameServerControl.Agent.IntegrationTests/ServerDeleteIntegrationTests.cs`

**Interfaces:**
- Fake implements `IWindowsRunnerServiceManager` and records `DeprovisionObservedServerPresent`, `DeprovisionCalls`, `CompleteCalls`; configurable `DeprovisionResult`.

- [ ] **Step 1: Successful delete: assert fake sees the definition during deprovision, response is 204, registry no longer contains it, and `CompleteDeprovision` was called once afterward.**
- [ ] **Step 2: Failed deprovision: return `(false, "blocked")`, assert response 502, definition still exists, and completion was not called.**
- [ ] **Step 3: Run the lifecycle tests and verify GREEN.**

### Task 6: Solution and CI integration

**Files:**
- Modify: `GameServerControl.sln`
- Modify: `.github/workflows/build.yml`

- [ ] **Step 1: Add the integration test project to the solution.**
- [ ] **Step 2: Add `dotnet test tests/GameServerControl.Agent.IntegrationTests/GameServerControl.Agent.IntegrationTests.csproj -c Release --no-restore` to Windows Build after the Release build.**
- [ ] **Step 3: On Linux, restore the integration test project, then run the same Release test command after the Agent build.**
- [ ] **Step 4: Run full Windows/Linux Build CI plus ARCH-01, BUG-02, PERF and supply-chain contracts.**
- [ ] **Step 5: Review changed files for test-only helpers, accidental production behavior changes and temporary workflows before marking the PR ready.**
