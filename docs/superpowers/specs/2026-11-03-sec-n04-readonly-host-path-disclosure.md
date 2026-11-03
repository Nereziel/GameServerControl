# SEC-N04 ReadOnly Host Path Disclosure

## Intent

Keep ReadOnly tokens useful for monitoring while preventing them from enumerating administrative host filesystem layout through discovery, install-job and mod-management GET responses.

## Root cause

Most authenticated GET endpoints are intentionally available to ReadOnly tokens. Three administrative surfaces currently expose host-path details without an Admin requirement:

- `GET /api/discover` returns `DiscoverResponse` entries with absolute `InstallPath` and `ExePath`, plus `LibrariesScanned` containing Steam/common host roots.
- `GET /api/servers/install/{jobId}` returns `InstallProgress.InstallPath` and bounded installer log lines for an installation workflow that only Admin can start.
- `GET /api/servers/{id}/mods` returns `ModsFolder`, which is an absolute host path such as `<GuestWorkingDir>/BepInEx/plugins` for BareMetal managers.

Backup listing was reviewed and does not expose absolute backup paths; its identifiers are archive/checkpoint names only. Public relay status was reviewed and exposes only a `hasApiToken` boolean, not the relay token value.

## Required behavior

- `GET /api/discover` is Admin-only.
- `GET /api/servers/install/{jobId}` is Admin-only, including nonexistent job IDs so authorization runs before job lookup.
- ReadOnly may continue to list installed mods, but `ModListResponse.ModsFolder` must be null for ReadOnly callers.
- Admin mod-list responses keep the existing absolute `ModsFolder` value for troubleshooting/reference.
- Relative `ModInfo.Files`, marketplace metadata, backup metadata, status/history and existing redacted server/config views remain available to ReadOnly.
- No write authorization semantics change.
- No secret values are introduced into responses.

## Chosen approach

Use endpoint-level Admin authorization for discovery and install-job progress because those endpoints exist solely for administrative installation workflows. Keep mod listing readable, but make `Program.cs` role-aware when constructing `ModListResponse`: expose the folder only to Admin callers.

Extend the security-access validation contract so future changes cannot accidentally reopen the two Admin-only GET endpoints or return `ModsFolder` unconditionally.

## Verification

- RED: ReadOnly `GET /api/discover` currently returns 200 instead of 403.
- RED: ReadOnly `GET /api/servers/install/does-not-exist` currently reaches the handler and returns 404 instead of 403.
- RED: for a Valheim BareMetal fixture, ReadOnly `GET /api/servers/alpha/mods` currently contains the absolute `BepInEx/plugins` folder.
- GREEN: Admin discovery remains usable; Admin nonexistent install-job lookup remains 404; Admin mod list retains `ModsFolder`; ReadOnly mod list retains mod metadata with `ModsFolder = null`.
