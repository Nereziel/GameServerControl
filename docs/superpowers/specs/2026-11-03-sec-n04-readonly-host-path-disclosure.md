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

- ReadOnly `GET /api/discover` is denied before host discovery runs.
- ReadOnly `GET /api/servers/install/{jobId}` is denied before job lookup, including nonexistent IDs.
- Admin discovery and install-job responses keep their existing behavior.
- ReadOnly may continue to list installed mods, but `ModListResponse.ModsFolder` must be null for ReadOnly callers.
- Admin mod-list responses keep the existing absolute `ModsFolder` value for troubleshooting/reference.
- Relative `ModInfo.Files`, marketplace metadata, backup metadata, status/history and existing redacted server/config views remain available to ReadOnly.
- No write authorization semantics change.
- No secret values are introduced into responses.

## Chosen approach

Extend `RoleEnforcementMiddleware`, which is already the centralized ReadOnly GET policy for server-list sanitization, config password redaction and raw-file denial. For ReadOnly GETs it denies the exact discovery route and the single-segment install-job route before `_next` executes. For the exact per-server `/mods` list route it buffers the normal response and rewrites only `modsFolder` to JSON null, leaving the mod array and support metadata unchanged.

Admin requests bypass these ReadOnly branches and therefore retain the existing endpoint responses. Extend the security-access validation contract so future changes cannot remove the discovery/install denial or return `ModsFolder` unconditionally.

## Verification

- RED: ReadOnly `GET /api/discover` returned 200 instead of 403.
- RED: ReadOnly `GET /api/servers/install/does-not-exist` reached the handler and returned 404 instead of 403.
- RED: for a Valheim BareMetal fixture, ReadOnly `GET /api/servers/alpha/mods` returned an absolute `BepInEx/plugins` folder.
- GREEN: Admin discovery remains usable; Admin nonexistent install-job lookup remains 404; Admin mod list retains `ModsFolder`; ReadOnly mod list retains mod metadata with `ModsFolder = null`.
