# Runtime Integration Tests Design

## Intent

Replace the highest-value source/string-only checks with runtime tests that execute the real ASP.NET Core pipeline and SignalR hub. The suite should catch authorization, routing, role enforcement, hub-group isolation and server-deletion lifecycle regressions that static validators cannot prove.

## Goals

- Run the Agent through `WebApplicationFactory<Program>` using the real middleware, startup behavior and endpoint mappings.
- Verify bearer authentication, Admin/ReadOnly role behavior, `/api/health`, and rejection of API query-string tokens.
- Verify SignalR console/log subscriptions are isolated per server at runtime.
- Verify the Runner protocol directly through typed assertions rather than reflection/string inspection only.
- Verify the server deletion endpoint deprovisions the Windows Runner before removing the definition, preserves the definition when deprovisioning fails, and completes the deprovision lifecycle after a successful delete.
- Keep tests deterministic and isolated from real host services, firewall, SCM and production state.

## Non-goals

- Do not create or modify real Windows services, firewall rules, Scheduled Tasks or host ACLs in CI.
- Do not replace the existing audit/source contracts; they remain cheap structural guards.
- Do not weaken or bypass normal Agent startup behavior for tests.
- Do not attempt the real-Windows-host E2E deployment exercise from roadmap item 2.

## Constraints

- Work only in `Nereziel/GameServerControl`.
- Test state lives in a unique temporary directory and is deleted after the fixture.
- The WebApplicationFactory host executes the same first-run credential migration/bootstrap path and periodic status/relay loop as production. The fixture supplies a non-placeholder Admin token, temporary data paths, an empty relay configuration and a fake Runner lifecycle implementation so those startup paths remain deterministic and cannot mutate host SCM/firewall/ACL state.
- Windows-specific Runner host mutations are represented behind a narrow lifecycle interface so endpoint ordering/failure behavior can be tested without real SCM access.
- SignalR integration uses TestServer-compatible LongPolling rather than requiring a real listening TCP port.
- The runtime suite runs on both Ubuntu and Windows to catch platform-sensitive validation and hosting behavior.

## Approaches considered

### 1. More source/string validators

Lowest cost, but it cannot prove middleware ordering, real authentication results, SignalR group membership or endpoint lifecycle sequencing. Rejected as the primary approach.

### 2. Hand-built TestServer that re-registers selected middleware/endpoints

Avoids touching `Program`, but duplicates the application composition and can pass while production wiring is broken. Rejected.

### 3. `WebApplicationFactory<Program>` over the real entry point — selected

Adds a conventional entry-point seam (`public partial class Program`) and a narrow `IWindowsRunnerServiceManager` abstraction. The test host executes production authentication, authorization, startup, middleware, endpoints and hub wiring; only real Windows Runner host mutations are replaced by an in-memory fake.

## Test architecture

Create `tests/GameServerControl.Agent.IntegrationTests` targeting `net8.0` with xUnit, `Microsoft.AspNetCore.Mvc.Testing` and `Microsoft.AspNetCore.SignalR.Client`.

`AgentTestFactory` owns a temporary data directory, seeds `servers.json`, configures a stable non-placeholder Admin token, creates ReadOnly managed tokens through the real `TokenRegistry`, and replaces only `IWindowsRunnerServiceManager` with `FakeWindowsRunnerServiceManager`.

HTTP tests exercise requests through `HttpClient`. SignalR tests create two real `HubConnection` clients against TestServer using LongPolling, subscribe each to a different server, start an actual `LogTailService` tail through the API, append a sentinel line, and assert only the matching subscriber receives it.

Runner protocol tests call `RunnerIdentity`, `RunnerManifest.Validate()` and `RunnerRequest.Validate()` directly and assert deterministic identity derivation, hostile-ID containment, cross-platform Windows-path validation, request-field constraints and console-input limits. These tests caught a real portability bug where `Path.GetFileName` on Linux did not treat a Windows backslash as a separator; validation now explicitly rejects both slash styles for process-name fields.

Deletion lifecycle tests drive `DELETE /api/servers/{id}` and use the fake Runner lifecycle to record whether the server definition still existed when deprovision was called. Failure injection must return 502 and leave the definition intact; success must deprovision first, delete second, then complete the lifecycle.

## Production seams

- Add `public partial class Program` after top-level statements for `WebApplicationFactory` discovery.
- Add `IWindowsRunnerServiceManager` containing only methods consumed by Agent runtime code. `WindowsRunnerServiceManager` implements it; DI maps the interface to the same singleton concrete instance.
- Change required consumers from the concrete manager to the interface so tests can substitute only host-mutating Runner lifecycle behavior.
- Keep first-run token handling, background status/relay refresh and all other startup behavior unchanged.
- Harden `RunnerManifest.StopProcessNames` validation to reject both `/` and `\\` separators independently of the OS running validation.

## CI

Add the integration test project to `GameServerControl.sln` so normal solution builds compile it. Run the full runtime suite in a dedicated `Runtime integration tests` matrix workflow on `ubuntu-24.04` and `windows-latest`. This avoids duplicating the same behavior suite inside both existing Build jobs while retaining the normal ARCH/BUG/PERF/supply-chain contracts and Windows/Linux release builds.