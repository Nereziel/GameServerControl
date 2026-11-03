# SEC-N04 Log Tail Subscriber Isolation

## Intent

Prevent a ReadOnly client from stopping the shared live-log stream for other authenticated clients. LogOnly live-tail lifecycle must follow SignalR subscriptions rather than unaffiliated ReadOnly HTTP start/stop requests.

## Root cause

`RoleEnforcementMiddleware` exempts `POST /api/servers/{id}/logs/tail` from the normal ReadOnly write denial because the endpoint is documented as controlling only the caller's live-log subscription. The endpoint does not carry SignalR connection identity, however. `LogTailService.Stop(serverId)` removes and cancels the single shared tail loop for that server, so any ReadOnly token can stop live logs for every subscriber.

The current Web UI exercises this global behavior directly: every LogOnly console opens a SignalR subscription and then calls HTTP `start`; closing any one console closes its WebSocket and calls HTTP `stop`, which terminates the server-wide tail even when other clients remain subscribed.

## Required behavior

- A LogOnly SignalR subscription must ensure the server live-tail loop is running without requiring a ReadOnly HTTP mutation.
- Each SignalR connection/server pair is registered at most once.
- Disconnecting one LogOnly subscriber must not stop the live tail while another subscriber to that server remains connected.
- When the last SignalR subscriber leaves, a subscriber-owned tail may stop so it does not become an orphan background loop.
- ReadOnly tokens must no longer be allowed to `POST /api/servers/{id}/logs/tail`; the generic ReadOnly mutation denial applies.
- The HTTP log-tail start/stop endpoint remains available to Admin tokens for backward-compatible explicit global lifecycle control.
- An Admin-started manual tail must not be stopped merely because the last SignalR subscriber disconnects.
- An explicit Admin `stop` remains a global administrative override and may stop the current tail even if subscribers are connected.
- Existing bounded caller-only backlog delivery remains unchanged: max 256 KiB read, 100 lines from the hub path, once per connection/server.
- Live log lines remain group-scoped to the subscribed server.
- No token/authentication semantics outside this endpoint change.

## Chosen approach

Move automatic live-tail ownership into the singleton `LogTailService`. Track subscriber connection IDs per server plus whether a tail was explicitly requested through the Admin HTTP API. `StatusHub.SubscribeConsole` registers LogOnly subscribers and `OnDisconnectedAsync` unregisters the connection. The first subscriber ensures a tail exists; the last subscriber cancels it only when no manual Admin start owns the tail.

Remove the ReadOnly control-plane exception from `RoleEnforcementMiddleware` and remove the Web UI's HTTP start/stop calls for LogOnly consoles. The browser will own only the SignalR connection; closing it naturally releases the subscription server-side.

## Verification

- RED authorization test: ReadOnly `POST /api/servers/{id}/logs/tail` is currently accepted but must be forbidden.
- RED lifecycle test: two LogOnly SignalR clients must receive live lines, and disconnecting the first must leave the second receiving subsequent lines without HTTP tail control.
- GREEN lifecycle test also verifies the tail stops after the last subscriber disconnects.
- Existing backlog isolation and repeated Admin start regression tests remain green.
- Full PR CI succeeds on Linux and Windows.
