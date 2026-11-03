# PERF-N04 Log Tail Start Amplification

## Intent

Prevent repeated authenticated `POST /api/servers/{id}/logs/tail` start requests from repeatedly rereading bounded log history and broadcasting it to every SignalR subscriber in the server group.

## Root cause

`LogTailService.Start` correctly keeps at most one live tail loop per server with `_active.GetOrAdd`, but it launches `EmitRecentAsync(serverId, path, 100, ...)` on every successful `Start` call, including when the tail is already active. `EmitRecentAsync` reads up to 256 KiB and broadcasts a marker plus up to 100 recent lines to the full server SignalR group. ReadOnly tokens are intentionally allowed to call the start/stop endpoint, so one authenticated caller can repeatedly trigger disk reads and group-wide replay traffic without creating additional tail loops.

## Required behavior

- A repeated `start` while the same server tail is already active must not reread or rebroadcast recent history.
- Every legitimate SignalR connection subscribing to a `LogOnly` console must still receive a bounded recent backlog once for that connection/server.
- Recent history must be sent only to the subscribing caller, not rebroadcast to all members of the server group.
- Repeated `SubscribeConsole` calls on the same SignalR connection/server must not replay backlog again.
- The live tail loop remains one-per-server and keeps existing EOF start, rotation handling, read bounds and live `logLine` group broadcasts.
- Existing HTTP start/stop API remains compatible; `start` continues to be idempotent and `stop` continues to stop the shared live tail.
- No authentication, role, token or filesystem path semantics change.

## Chosen approach

Separate live-tail lifecycle from initial-history delivery. `LogTailService.Start` will only ensure the live loop exists. A bounded recent-history method will return `LogLine` values without broadcasting them. `StatusHub.SubscribeConsole` will use that method for `LogOnly` profiles and send the returned lines through `Clients.Caller`, guarded by connection-scoped state so the backlog is delivered once per server on that SignalR connection.

This follows the existing stdin-console pattern, where initial state/history is sent to the caller while ongoing output is broadcast to the server group.

## Verification

- Integration RED: after one client has received the initial backlog, a second HTTP `start` must not produce another copy of the same history lines.
- Integration GREEN: a `LogOnly` subscriber still receives its initial history through the hub.
- Full runtime/build and existing contract workflows remain green.
