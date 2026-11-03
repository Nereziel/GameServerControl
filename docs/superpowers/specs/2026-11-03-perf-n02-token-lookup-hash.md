# PERF-N02 Token Lookup Hashing

## Intent

Remove per-token hashing and avoid per-token temporary hash allocations during managed-token lookup without changing authentication semantics or persisted token format.

## Problem

`TokenRegistry.Lookup` calls `HashMatches(presented, token.TokenHash)` for every managed token. `HashMatches` recomputes SHA-256 of the same presented bearer token and allocates a decoded expected hash on every iteration. A miss therefore performs work and allocations proportional to the number of managed tokens before authentication can reject the request.

## Required behavior

- Compute the SHA-256 digest of a presented managed credential once per `Lookup` call.
- Keep comparison constant-time.
- Do not change the legacy `Agent:ApiToken` fallback behavior.
- Do not change `tokens.json`, token creation, token metadata, roles, prefixes, deletion, or migration behavior.
- Managed-token lookup results must remain identical for hits and misses.
- Repeated misses against a registry with many managed tokens must not allocate proportionally to token count.

## Approaches

### Chosen: hash once and compare normalized hex in fixed time

Compute the presented SHA-256 hash once after the legacy-token check and normalize it to the same lowercase 64-character hex representation already used in memory. Compare that fixed-length UTF-16 hex value to each validated stored hash with `CryptographicOperations.FixedTimeEquals` over `MemoryMarshal.AsBytes` spans. This removes repeated SHA-256 work and per-token byte-array allocations while keeping the stored representation unchanged and remaining compatible with .NET 8.

### Rejected: build an in-memory dictionary keyed by token hash

This would make lookup O(1), but dictionary lookup is not constant-time with respect to secret values and would introduce a second mutable index that must stay synchronized with reload/create/delete operations.

### Rejected: cache decoded hashes beside persisted records

This removes per-token decoding allocations, but requires a parallel runtime model or cache lifecycle. Fixed-time comparison of the already-normalized in-memory hex strings is smaller and sufficient for PERF-N02.

## Verification

Add a regression test that compares managed-token miss allocations for a one-token registry and a large registry after warm-up. The large registry must remain within a small fixed allocation delta, proving that lookup no longer allocates per managed token. Existing authentication and token-storage tests must remain green.
