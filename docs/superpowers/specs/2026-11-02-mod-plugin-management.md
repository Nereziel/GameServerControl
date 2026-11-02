# Mod / Plugin Management Improvements

## Goal
Make the existing mod manager practical for day-to-day administration without changing provider/download architecture.

## Required behavior
- Keep the existing Installed / Browse / Updates workflow.
- Installed mods expose a **Configure** action.
- Configure discovers allowed text config files through the existing `/api/servers/{id}/files` API; it must not bypass the config-file allowlist or expose arbitrary filesystem access.
- Config discovery must work when a mod/plugin stores configuration in different supported folders. Matching files for the selected mod are shown first; all discovered `Mods / plugins` config files remain available as a fallback so unusual naming conventions are still manageable.
- Editing uses existing file content/write/validate/history APIs with SHA conflict protection. No new unrestricted file endpoint is introduced.
- Installed mods with an HTTP(S) source expose a Source action.
- Bulk update continues processing remaining mods when one update fails, then reports a summary of successes/failures.
- Install/update/remove operations warn when a running server may need restart.
- Cloudflare mode uses the same WebUI and APIs; large mod archives continue to be downloaded directly by the Agent/source, not proxied through the Worker.

## Non-goals
- Dependency resolver.
- Generic disk browser.
- Provider-specific schema/forms for every mod.
- Automatic restart of a running server.
