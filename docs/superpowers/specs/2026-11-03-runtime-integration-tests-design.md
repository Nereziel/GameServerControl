# Runtime Integration Tests Design

## Intent

Replace the highest-value source/string-only checks with runtime tests that execute the real ASP.NET Core pipeline and SignalR hub. The suite should catch authorization, routing, role enforcement, hub-group isolation and server-deletion lifecycle regressions that static validators cannot prove.

## Goals

- Run the Agent through `WebApplicationFactory<Program>` using the real middleware and endpoint mappings.
- Verify bearer authentication, Admin/ReadOnly role behavior, `/api/health`, and rejection of API query-string tokens.
- Verify SignalR console/log subscriptions are isolated per server at runtime.
- Verify the Runner protocol directly through typed assertions rather than reflection/string inspection only.
- Verify the server deletion endpoint deprovisions the Windows Runner before removing the definition, preserves the definition when deprovisioning fails, and completes the deprovision lifecycle after a successful delete.
- Keep tests deterministic and isolated from host services, firewall, SCM and production state.

## Non-goals

- Do not create or modify real Windows services, firewall rules, Scheduled Tasks or host ACLs in CI.
- Do not replace the existing audit/source contracts; they remain cheap structural guards.
- Do not change API behavior or Runner behavior to satisfy tests.
- Do not attempt the real-Windows-host E2E deployment exercise from roadmap item 2.

## Constraints

- Work only in `Nereziel/GameServerControl`.
- Test state must live in a temporary directory and be deleted after the test fixture.
- `Testing` environment disables first-run credential persistence and the 30-second background status/relay loop, but production environments keep current behavior.
- Tests must run on the normal Linux CI job. Windows-specific host operations are represented behind a narrow lifecycle interface so endpoint ordering/failure behavior can be tested without SCM access.
- SignalR integration uses TestServer-compatible HTTP transport rather than requiring a real listening TCP port.

## Approaches considered

### 1. More source/string validators

Lowest cost, but it cannot prove middleware ordering, real authentication results, SignalR group membership or endpoint lifecycle sequencing. Rejected as the primary approach.

### 2. Hand-built TestServer that re-registers selected middleware/endpoints

Avoids touching `Program`, but duplicates the application composition and can pass while production wiring is broken. Rejected.

### 3. `WebApplicationFactory<Program>` over the real entry point — selected

Adds only a conventional testability seam (`public partial class Program`) and skips startup side effects in the `Testing` environment. It executes the production authentication, authorization, middleware, endpoints and hub. A small `IWindowsRunnerServiceManager` abstraction lets lifecycle tests replace host mutations with an in-memory fake while production still resolves the concrete manager.

## Test architecture

Create `tests/GameServerControl.Agent.IntegrationTests` targeting `net8.0` with xUnit, `Microsoft.AspNetCore.Mvc.Testing` and `Microsoft.AspNetCore.SignalR.Client`.

`AgentTestFactory` owns a temporary data directory, seeds `servers.json`, configures a stable Admin token, creates ReadOnly managed tokens through the real `TokenRegistry`, and replaces only `IWindowsRunnerServiceManager` with `FakeWindowsRunnerServiceManager`.

HTTP tests exercise requests through `HttpClient`. SignalR tests create two real `HubConnection` clients against TestServer using LongPolling, subscribe each to a different server, start an actual `LogTailService` tail through the API, append a sentinel line, and assert only the matching subscriber receives it.

Runner protocol tests call `RunnerIdentity`, `RunnerManifest.Validate()` and `RunnerRequest.Validate()` directly and assert deterministic identity derivation, hostile-ID containment, path validation, request-field constraints and console-input limits.

Deletion lifecycle tests drive `DELETE /api/servers/{id}` and use the fake Runner lifecycle to record whether the server definition still existed when deprovision was called. Failure injection must return 502 and leave the definition intact; success must deprovision first, delete second, then complete the lifecycle.

## Production seams

- Add `public partial class Program` after top-level statements for `WebApplicationFactory` discovery.
- Guard first-run token generation and the periodic background refresh loop with `!builder.Environment.IsEnvironment("Testing")`.
- Add `IWindowsRunnerServiceManager` containing only methods currently consumed by Agent runtime code. `WindowsRunnerServiceManager` implements it; DI maps the interface to the same singleton concrete instance.
- Change consumers from the concrete manager to the interface where replacement is required. No method semantics change.

## CI

Add the integration test project to `GameServerControl.sln` and run `dotnet test tests/GameServerControl.Agent.IntegrationTests/GameServerControl.Agent.IntegrationTests.csproj -c Release --no-restore` in both normal Build jobs after restore/build prerequisites. Existing ARCH/BUG/PERF/supply-chain contracts remain in place.