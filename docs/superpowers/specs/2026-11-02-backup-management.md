# Backup Management Design

## Goal

Make the Backups workspace safe and useful for both local and Cloudflare administration without moving backup archives through the browser. Backup creation, retention, restore and deletion remain Agent-side operations.

## Scope

- BareMetal servers use ZIP archives under the existing Agent backup root.
- Hyper-V servers use GameServerControl-owned VM checkpoints.
- Manual backups may have a human-readable label; the archive/checkpoint identifier remains machine-safe.
- Each server may override backup retention; the Agent-wide `Agent:BackupRetention` remains the default and falls back to 10.
- The UI shows kind, label, timestamp, size and backup warnings/metadata where available.
- Scheduled backups continue using the existing lightweight local API callback.

## Safety requirements

1. Never accept a backup archive name that can escape the server backup directory.
2. Reject ZIP entries containing rooted paths, `..` traversal or paths outside configured save-directory prefixes.
3. Restore requires the server to be stopped. Hyper-V checkpoint restore requires the VM to be off.
4. A pre-restore safety backup/checkpoint is mandatory. If it cannot be created, restore aborts before modifying current data.
5. BareMetal restore extracts into staging directories first, then swaps the configured save directories; files not present in the selected backup must not survive the restore.
6. If a directory swap fails, best-effort rollback restores the original directories.
7. Retention prunes only GameServerControl-owned backups. Pre-restore safety copies are retained separately and are never counted as normal backups.
8. Manual delete and restore operate only on backups returned by the Agent for that server.

## Metadata

BareMetal ZIPs use a sidecar JSON metadata file next to the archive. Metadata records label, kind, creation time, file count and skipped locked-file count. Missing sidecars are treated as legacy backups and remain usable.

## API/UI

- Keep `POST /api/servers/{id}/backup` for existing automation/quick backup compatibility.
- Add `POST /api/servers/{id}/backups` with an optional label for manual named backup creation.
- Add `GET/PUT /api/servers/{id}/backups/settings` for the per-server retention override.
- Existing list/restore/delete routes remain stable.
- Web UI Backups tab creates named backups, edits retention, disables unsafe restore actions while running, and explains the mandatory safety copy.

## Non-goals

- Browser download/upload of large backup archives.
- Remote object storage or cloud backup replication.
- Exposing arbitrary filesystem paths.
- Managing Hyper-V checkpoints not created by GameServerControl.
