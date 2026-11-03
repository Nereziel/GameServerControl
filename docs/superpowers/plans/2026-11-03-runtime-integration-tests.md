# Runtime Integration Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add runtime integration coverage for Agent authentication/roles/health, SignalR server isolation, Runner protocol behavior, and Runner deprovision/delete lifecycle.

**Architecture:** Use `WebApplicationFactory<Program>` against the real Agent entry point and keep normal startup behavior active. Add only a conventional public `Program` entry-point seam and an `IWindowsRunnerServiceManager` abstraction so tests can replace real Windows host mutations while exercising production middleware/endpoints/SignalR wiring.

**Tech Stack:** .NET 8, ASP.NET Core `WebApplicationFactory`, TestServer, xUnit, SignalR .NET client.

**Spec:** `docs/superpowers/specs/2026-11-03-runtime-integration-tests-design.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Do not touch real Windows SCM, firewall, Scheduled Tasks or ACLs from integration tests.
- Test data lives under a unique temporary directory and is cleaned up.
- First-run token handling and the periodic status/relay loop remain active in the integration host exactly as in production.
- Existing ARCH/BUG/PERF/supply-chain contracts remain enabled.

## Review Focus

- Test configuration must not create a special production startup mode.
- SignalR tests must prove a beta subscriber never receives an alpha sentinel, not merely that alpha receives it.
- ReadOnly authorization must be tested through the real middleware, including allowed GET and denied mutation.
- Delete failure must leave the server definition intact; successful delete must call deprovision before removal and completion after removal.
- The Runner request schema must remain incapable of carrying executable/path/argument launch metadata.

## Implementation status

The initial RED run compiled the Agent but failed the integration project because `Program` was not public and `IWindowsRunnerServiceManager` did not exist. After adding those seams, the first runtime suite exposed a real cross-platform Runner validation defect: Linux did not treat `\\` as a separator when validating Windows process-name fields. The production validation now rejects both separator styles. The full 17-test runtime suite subsequently passed on both Ubuntu and Windows. A self-review then removed an unnecessary `Testing`-environment startup bypass; the runtime suite also passed with the normal production startup path active.

---

### Task 1: Testable Agent entry point and Runner lifecycle interface

**Files:**
- Create: `src/GameServerControl.Agent/Hyperv/IWindowsRunnerServiceManager.cs`
- Modify: `src/GameServerControl.Agent/Hyperv/WindowsRunnerServiceManager.cs`
- Modify: `src/GameServerControl.Agent/Hyperv/LocalProcessService.cs`
- Modify: `src/GameServerControl.Agent/Program.cs`

- [x] Write tests that reference `Program` and substitute `IWindowsRunnerServiceManager`.
- [x] Verify RED on inaccessible `Program` / missing Runner interface.
- [x] Add the interface, map it to the existing singleton manager, switch required consumers, expose `PollConsoleStateAsync`, and add `public partial class Program`.
- [x] Keep first-run and background refresh startup behavior active; contain host mutations through temp configuration and the fake Runner.
- [x] Verify the integration host runs the runtime suite successfully.

### Task 2: HTTP authentication, role and health integration tests

**Files:**
- `tests/GameServerControl.Agent.IntegrationTests/AgentTestFactory.cs`
- `tests/GameServerControl.Agent.IntegrationTests/AuthenticationIntegrationTests.cs`
- `tests/GameServerControl.Agent.IntegrationTests/TestCollection.cs`

- [x] Unauthenticated `/api/health` -> 401; Admin bearer -> 200 with `ok=true`.
- [x] `/api/health?access_token=<admin>` -> 401 while header authentication succeeds.
- [x] ReadOnly GET `/api/servers` -> 200 without seeded RCON/webhook plaintext.
- [x] ReadOnly POST `/api/servers/reload` -> 403; Admin POST -> 200.
- [x] Verify the tests pass through the real middleware pipeline.

### Task 3: Runtime SignalR server-group isolation

**Files:**
- `tests/GameServerControl.Agent.IntegrationTests/SignalRIsolationIntegrationTests.cs`
- `tests/GameServerControl.Agent.IntegrationTests/AgentTestFactory.cs`

- [x] Connect two authenticated TestServer LongPolling clients and subscribe to alpha/beta independently.
- [x] Start alpha tail through the real API.
- [x] Append a unique alpha sentinel and verify alpha receives it.
- [x] Verify beta does not receive the alpha sentinel within a bounded observation window.
- [x] Stop the tail and connections in cleanup.

### Task 4: Typed Runner protocol behavior tests

**Files:**
- `tests/GameServerControl.Agent.IntegrationTests/RunnerProtocolTests.cs`
- `src/GameServerControl.Shared/RunnerProtocol.cs`

- [x] Assert deterministic/distinct/path-safe Runner identities.
- [x] Assert rooted Windows manifest paths validate and relative paths fail.
- [x] Assert path-valued stop-process names fail for both `/` and `\\` separators; fix the cross-platform validation defect found by RED testing.
- [x] Assert the Runner request public schema contains only `Command`, `Force`, `ConsoleInput` and cannot carry launch metadata.
- [x] Assert bounded single-line console input validation.

### Task 5: Runner deprovision/delete lifecycle integration tests

**Files:**
- `tests/GameServerControl.Agent.IntegrationTests/FakeWindowsRunnerServiceManager.cs`
- `tests/GameServerControl.Agent.IntegrationTests/ServerDeleteIntegrationTests.cs`

- [x] Successful delete observes definition present during deprovision, returns 204, removes definition, then completes deprovision.
- [x] Failed deprovision returns 502, preserves definition, and does not call completion.
- [x] Verify both paths through the real DELETE endpoint.

### Task 6: Solution and CI integration

**Files:**
- Modify: `GameServerControl.sln`
- Create: `.github/workflows/runtime-integration.yml`

- [x] Add the integration test project to the solution so normal solution builds compile it.
- [x] Add a dedicated Ubuntu/Windows `Runtime integration tests` matrix workflow running the full Release test suite.
- [x] Keep runtime execution out of the existing Build jobs to avoid duplicate four-way execution while retaining normal Windows/Linux build/publish coverage.
- [ ] Run final Windows/Linux Build CI plus runtime integration, ARCH-01, BUG-02, PERF and supply-chain contracts on the final normal-authored HEAD.
- [ ] Review final changed files for temporary helpers, accidental production behavior changes and review feedback before marking PR ready.
