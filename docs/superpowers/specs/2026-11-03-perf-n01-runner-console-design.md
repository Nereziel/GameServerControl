# PERF-N01 Runner Console Polling Design

## Intent

Remove the new Windows Runner console polling regression without replacing the existing Runner architecture. A console subscriber should receive the bounded initial history once, then only incremental lines. Runner polling must stop when the last SignalR connection watching that server disconnects.

## Goals

- Keep at most one Agent-side Runner console pump per server.
- Preserve the current initial bounded console history for a new subscriber.
- Add a cursor to `ConsoleState` polling so subsequent IPC responses contain only lines newer than the caller's last sequence.
- Recover immediately with bounded history if the Runner restarts and its sequence counter resets below the Agent pump cursor.
- Stop the per-server pump after its last subscriber disconnects.
- Support one SignalR connection subscribing to multiple servers and multiple connections subscribing to one server.
- Keep the existing 250 ms active polling cadence; this fix targets payload amplification and orphan polling, not a transport rewrite.

## Non-goals

- Do not replace named-pipe request/response with a streaming pipe protocol.
- Do not create one Runner poll loop per browser connection.
- Do not change Linux/local console behavior.
- Do not increase the existing console buffer or protocol response limits.

## Approaches considered

### Chosen: cursor-based IPC + subscriber reference tracking

Extend `RunnerRequest` with an optional non-negative `AfterSequence` that is valid only for `ConsoleState`. Initial console state omits the cursor and returns the bounded history. The Agent pump sends its last seen sequence and receives only newer lines. `LocalProcessService` tracks SignalR connection IDs per Windows server, starts one pump for the first subscriber, and cancels it when the final subscriber is removed. `StatusHub.OnDisconnectedAsync` performs cleanup.

This is the smallest change that fixes both causes of PERF-N01 while retaining the existing Runner request/response model.

### Rejected: one poller per SignalR connection

Simpler lifecycle ownership, but IPC and JSON work scales with browser connections instead of servers and duplicates identical Runner polling.

### Rejected: push/streaming Runner pipe

Would remove polling entirely, but requires a long-lived protocol, reconnection semantics, fan-out and broader service-lifecycle changes. That is disproportionate to this regression.

## Protocol behavior

`RunnerRequest.AfterSequence` is nullable. When null, `ConsoleState` returns the current bounded history. When present, it must be zero or positive and only accompanies `RunnerCommand.ConsoleState`; the Runner returns only lines whose `Sequence` is greater than the cursor while `LastSequence` still reports the current global console sequence. If the supplied cursor is greater than the Runner's current `LastSequence`, the Runner treats that as a sequence reset and returns the current bounded history so the Agent resynchronizes in the same poll.

## Subscription lifecycle

The SignalR subscription remains server-group based. For Windows stdin consoles, `SubscribeConsole` obtains the initial state and registers the caller connection with `LocalProcessService`. Registration is idempotent for the same connection/server pair. The first active connection starts the single server pump from the initial state's `LastSequence`.

On disconnect, `StatusHub.OnDisconnectedAsync` removes that connection from every tracked server. If a server has no remaining subscribers, its pump cancellation token is cancelled. Pump cleanup must remove only its own state so a reconnect cannot have a newly started pump removed by an older pump's `finally` block.

## Verification

- Protocol tests prove the cursor exists, is command-scoped and rejects negative values.
- Runner behavior test proves initial history is full, cursor polling returns only newer lines, and a cursor beyond the current sequence triggers immediate bounded resynchronization.
- Subscription lifecycle test proves the pump stops after the last subscriber disconnects.
- Existing runtime integration, ARCH-01 and Build workflows remain green.
