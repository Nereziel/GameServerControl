# QA-N01 Agent Platform Analyzer Contract

## Intent

Remove the persistent `CA1416` warning emitted by the Agent build for the Unix-only `FileStreamOptions.UnixCreateMode` use in `SecretProtector`, without suppressing the analyzer and without changing secret-protection behavior.

## Root cause

`SecretProtector.Protect` selects DPAPI on Windows and the AES-GCM key-file path on non-Windows systems. The Unix key creation method itself does not contain a local platform guard, so the platform analyzer treats its `UnixCreateMode` assignment as reachable on Windows even though normal call flow reaches it only from the non-Windows branch.

`SecretFilePermissions.WriteAllTextAtomic` uses the same Unix-only API without a warning because the `OperatingSystem.IsWindows()` split is in the same method as the `UnixCreateMode` assignment.

## Required behavior

- Do not suppress `CA1416` with pragma, NoWarn, or analyzer configuration.
- Add an explicit platform guard in the Unix key creation path so the analyzer can prove the code is non-Windows.
- Preserve Windows DPAPI behavior and Unix AES-GCM behavior.
- Preserve the 0600 Unix master-key creation mode and existing race handling.
- Add a CI contract that builds the Agent with `CA1416` treated as an error, preventing regression.
- Keep the existing SEC-08 secret-protection validation green on Windows and Linux.

## Verification

- RED: Agent build with `CA1416` promoted to error fails on current code at `SecretProtector.cs` `UnixCreateMode`.
- GREEN: the same build succeeds after the local guard is added.
- Full PR CI remains green on Windows and Linux.
