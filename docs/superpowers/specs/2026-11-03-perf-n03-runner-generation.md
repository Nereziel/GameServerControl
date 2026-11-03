# PERF-N03 Runner Console Generation

## Intent

Prevent Windows Runner console lines from being silently skipped when the Runner service restarts and its new sequence counter catches up to or exceeds the Agent pump's previous cursor before the next poll.

## Root cause

Console delta identity is currently only a monotonically increasing `Sequence` that starts from zero in each Runner process. The Runner returns full history only when `AfterSequence > _nextSequence`, while the Agent resets its cursor only when `snapshot.LastSequence < lastSequence`. If a restarted Runner emits enough lines before the first poll, neither condition detects the restart and the early lines of the new Runner generation are filtered out.

## Required behavior

- Every Runner process instance exposes an opaque console generation ID for its lifetime.
- Console poll requests may carry the generation associated with the caller's sequence cursor.
- A Runner receiving a cursor from a different generation returns its bounded full console history immediately, even when the requested sequence is less than or equal to the new `LastSequence`.
- The Agent resets its local sequence cursor when a non-null snapshot generation changes, then consumes the returned full history.
- Existing `LastSequence < lastSequence` reset behavior remains as a fallback for older Runner responses that do not include generation metadata.
- Initial console state remains a bounded full-history response.
- Request/response JSON changes are additive and nullable so Agent/Runner version skew degrades to the previous sequence-only behavior rather than breaking IPC.
- Existing response-size, line-count and character-count bounds remain unchanged.

## Approaches

### Chosen: generation in both request and snapshot

Add nullable `ConsoleGeneration` to `RunnerRequest` and nullable `Generation` to `RunnerConsoleSnapshot`. `RunnerProcessHost` creates one generation ID per host instance and returns full bounded history whenever a poll supplies a different non-null generation. The Agent tracks the snapshot generation alongside `lastSequence` and resets the sequence cursor on generation change.

This detects the restart in the first poll and does not require an extra round trip that could allow a high-volume console to evict additional lines before resync.

### Rejected: generation only in snapshots

The Agent could notice the change after receiving a delta response and issue a second poll from sequence zero. This leaves a race between the first and second polls and can still lose bounded history under sufficiently high output volume.

### Rejected: persist the sequence counter across Runner restarts

Persisting cursor state adds disk lifecycle and crash-consistency concerns and still does not identify which buffered lines belong to which process instance. A process-generation ID is smaller and directly models the reset boundary.

## Compatibility

Generation fields are nullable and appended to existing record constructors. A new Agent talking to an old Runner receives `Generation = null` and keeps the old sequence fallback. An old Agent ignores the new JSON properties and continues sequence-only polling.

## Verification

- Protocol test proves generation metadata is additive and console-state-only.
- Runner test proves a generation mismatch forces full bounded history even when the new sequence has already passed the old cursor.
- Agent pump regression proves lines from a changed generation are broadcast from sequence 1 rather than filtered by the stale cursor.
- Full Linux/Windows runtime and build workflows remain green.