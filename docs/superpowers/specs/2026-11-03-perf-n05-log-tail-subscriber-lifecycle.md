# PERF-N05 Log Tail Subscriber Lifecycle

## Intent

Make LogOnly live-log lifetime follow active SignalR subscribers instead of unauthenticated-to-the-tail HTTP start/stop side effects, so one ReadOnly client cannot interrupt another client's live log stream.

## Root cause

`LogTailService` owns one shared tail loop per server. `POST /api/servers/{id}/logs/tail` calls `Start` or `Stop` on that shared loop. `RoleEnforcementMiddleware` exempts every POST matching that path from Admin enforcement, so a ReadOnly token can invoke both actions. The Web UI starts the shared loop when a LogOnly console opens and globally stops it when that one UI instance closes. With two clients on the same server, either client's close can cancel the loop for both.

`StatusHub.SubscribeConsole` already knows the authenticated SignalR connection ID and server ID, but currently only joins the group and sends caller-only backlog; it does not register live-log ownership. `OnDisconnectedAsync` only cleans up stdin/Runner subscriptions.

## Required behavior

- A LogOnly `SubscribeConsole` must ensure the server's live-log tail is running and register that SignalR connection as a subscriber.
- Repeating `SubscribeConsole` for the same connection/server must not double-count the subscriber.
- Disconnecting one of multiple LogOnly subscribers must not stop the shared tail.
- Disconnecting the final LogOnly subscriber must stop the automatically managed tail so no orphan polling loop remains.
- A ReadOnly token must not be able to start or stop the shared tail through the HTTP control endpoint.
- The HTTP start/stop endpoint remains available to Admin tokens for backward/manual compatibility.
- Admin `stop` remains an explicit privileged force-stop of the shared tail.
- Existing caller-only recent backlog behavior remains once per connection/server.
- Existing per-server SignalR group isolation, EOF start, rotation handling, 256 KiB backlog cap, 100-line UI backlog, and live `logLine` broadcasts remain unchanged.
- Existing stdin/Runner console subscription lifecycle remains unchanged.

## Chosen approach

Move automatic LogOnly lifetime ownership into `LogTailService` subscriber tracking. Add connection-aware subscribe/unsubscribe operations used only by `StatusHub`; automatically created tails are stopped when their final subscriber leaves. Keep `Start`/`Stop` as Admin manual controls, with `Stop` retaining force-stop semantics.

Remove the ReadOnly-control POST exemption from `RoleEnforcementMiddleware`, making `/logs/tail` POST follow the normal Admin-only write rule. Remove the Web UI's HTTP start/stop calls; the SignalR subscription and disconnect become the lifecycle boundary.

Tail creation must avoid starting background work inside a `ConcurrentDictionary.GetOrAdd` value factory, because factories may run more than once under contention. Only the handle that actually wins insertion may start a loop.

## Verification

- RED: a ReadOnly POST `{ action: "stop" }` to `/api/servers/alpha/logs/tail` currently returns 200; required status is 403.
- RED: two LogOnly hub subscribers currently do not automatically own/start the tail; required behavior is tailing=true after subscribe, still true after one disconnect, live delivery to the remaining client, then tailing=false after the final disconnect.
- GREEN: existing backlog-once, server-group isolation, Runner console and auth integration tests continue to pass on Ubuntu and Windows.
