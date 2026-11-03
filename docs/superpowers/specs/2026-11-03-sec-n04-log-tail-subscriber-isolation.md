# SEC-N04 Log Tail Subscriber Isolation

## Intent

Prevent a ReadOnly client from stopping the shared live-log stream for other authenticated clients. LogOnly live-tail lifecycle must follow SignalR subscriptions rather than unaffiliated ReadOnly HTTP start/stop requests.

## Root cause

`RoleEnforcementMiddleware` exempts `POST /api/servers/{id}/logs/tail` from the normal ReadOnly write denial because the endpoint is documented as controlling only the caller's live-log subscription. The endpoint does not carry SignalR connection identity, however. `LogTailService.Stop(serverId)` removes and cancels the single shared tail loop for that server, so any ReadOnly token can stop live logs for every subscriber.

The current Web UI exercises this global behavior directly: every LogOnly console opens a SignalR subscription and then calls HTTP `start`; closing any one console closes its WebSocket and calls HTTP `stop`, which terminates the server-wide tail even when other clients remain subscribed.

## Required behavior

- A LogOnly SignalR subscription must ensure the server live-tail loop is running without requiring an HTTP mutation for correctness.
- Each SignalR connection/server pair is registered at most once.
- Disconnecting one LogOnly subscriber must not stop the live tail while another subscriber to that server remains connected.
- When the last SignalR subscriber leaves, a subscriber-owned tail may stop so it does not become an orphan background loop.
- Existing ReadOnly HTTP `POST /api/servers/{id}/logs/tail` compatibility remains, but ReadOnly `start` and `stop` are side-effect free: they may report current tail state but must not create, cancel, or claim manual ownership of a tail.
- The same HTTP endpoint remains fully functional for Admin tokens as backward-compatible explicit global lifecycle control.
- An Admin-started manual tail must not be stopped merely because the last SignalR subscriber disconnects.
- An explicit Admin `stop` remains a global administrative override and may stop the current tail even if subscribers are connected.
- Existing bounded caller-only backlog delivery remains unchanged: max 256 KiB read, 100 lines from the hub path, once per connection/server.
- Live log lines remain group-scoped to the subscribed server.
- No token/authentication semantics outside this endpoint change.

## Chosen approach

Move automatic live-tail ownership into the singleton `LogTailService`. Track subscriber connection IDs per server plus whether a tail was explicitly requested through the Admin HTTP API. `StatusHub.SubscribeConsole` registers LogOnly subscribers and `OnDisconnectedAsync` unregisters the connection. The first subscriber ensures a tail exists; the last subscriber cancels it only when no manual Admin start owns the tail.

Keep the existing ReadOnly control-plane exception and Web UI calls for compatibility, but make the endpoint role-aware: ReadOnly calls validate the requested action and return current tail state without invoking `Start` or `Stop`; Admin calls retain current global semantics. This avoids coupling HTTP requests to SignalR connection identity and avoids breaking existing browser/client behavior.

## Ruling

The initial design proposed returning 403 for ReadOnly log-tail POSTs and removing the Web UI HTTP calls. During implementation review, a compatibility-preserving path was found: keep those calls side-effect free for ReadOnly while SignalR owns the actual automatic lifecycle. This fixes the cross-client availability problem with a smaller surface change and preserves existing clients. The authorization boundary remains safe because ReadOnly calls no longer mutate shared state.

## Verification

- RED lifecycle test: two LogOnly SignalR clients must receive live lines, and disconnecting the first must leave the second receiving subsequent lines without relying on HTTP ownership.
- RED compatibility-isolation test: a ReadOnly HTTP `stop` while subscribers are active must not stop their shared tail.
- GREEN lifecycle test also verifies the tail stops after the last subscriber disconnects when no Admin manual owner exists.
- Existing backlog isolation and repeated Admin start regression tests remain green.
- Full PR CI succeeds on Linux and Windows.
