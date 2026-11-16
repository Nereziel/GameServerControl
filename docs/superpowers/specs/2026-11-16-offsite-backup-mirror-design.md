# Phase C: opt-in off-machine backup mirror (2026-11-16)

## Intent and design
Expose a **disabled by default** `Agent:OffsiteBackupRoot` option. Administrators can mount a trusted SMB/NFS share (or an off-machine volume) on the Agent host and set this absolute directory as the destination. No cloud SDK, external credentials or provider API is needed. Neither Cloudflare Worker nor GitHub receives backup data.

On successful complete BareMetal backups, mirror the .zip and .meta.json to `<OffsiteBackupRoot>/<server-id>/<archive name>`. Verify SHA-256 after copying, then atomically rename staging files. A corrupt existing remote copy must never be overwritten silently. Absent or incomplete backup metadata is rejected. Any mirroring failure must leave the local archive intact and report failure, not a successful remote-backup claim.

Copying is streaming and has **no size limit** added; do not change the existing archive retention policy. Remote files are intentionally not pruned by local retention; independent off-machine recovery must remain possible. Operators must monitor free space and periodically test restores. Backups are not sent to a third party automatically.

## Alternatives considered
- R2/S3 API upload: integrates with cloud providers but requires credentials, provider-specific error handling and explicit external data transfer; defer until owner opts in.
- Mounted network folder: reuses OS-managed authentication, limits data sharing, and can be tested locally. Chosen.
- Only manual export: less risk but does not cover unattended backups.

## Boundaries and non-goals
- No Hyper-V checkpoint mirroring (binary checkpoint export needs an independent snapshot workflow).
- No changing backup size caps, archive restore code, local retention counts, existing credential stores, PC1 or Cloudflare production.
- Separate PR for Minecraft Forge/NeoForge and another for monitoring/diagnostics.
