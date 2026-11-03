# SEC-N04 SecretProtector Platform Guard

## Intent

Remove the CA1416 platform-compatibility warning from `SecretProtector` without weakening Unix key-file permissions or changing secret envelope behavior.

## Root cause

`GetOrCreateUnixKey` assigns `FileStreamOptions.UnixCreateMode`, an API unsupported on Windows, without an explicit platform guard in the same method. Callers currently reach it only through the Unix protection path, but the platform analyzer cannot prove that invariant at the assignment site and emits CA1416.

## Required behavior

- Keep Unix key creation atomic with `UnixCreateMode = UserRead | UserWrite`.
- Keep the existing post-create `SecretFilePermissions.RestrictExisting` hardening.
- Keep Windows DPAPI behavior unchanged.
- Keep Unix AES-GCM envelope, key format and key path unchanged.
- `GetOrCreateUnixKey` must fail explicitly if ever invoked on Windows rather than reaching a Windows-unsupported API.
- Build CI must treat CA1416 as an error so future unguarded platform-specific calls are rejected.
- Do not promote unrelated warnings to errors.

## Chosen approach

Add an explicit `OperatingSystem.IsWindows()` guard at the beginning of `GetOrCreateUnixKey`, before any `UnixCreateMode` assignment. Keep the existing atomic file creation and permission mode. Update the existing Build workflow to pass `-p:WarningsAsErrors=CA1416` for Agent compilation on both Windows and Linux.

## Verification

- RED: Build workflow with `CA1416` promoted to error fails on the current implementation at the `UnixCreateMode` assignment.
- GREEN: the same Build workflow succeeds after the explicit guard.
- Existing runtime integration and contract workflows remain green.
