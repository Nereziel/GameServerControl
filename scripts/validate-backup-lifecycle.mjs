import fs from 'node:fs';

const backup = fs.readFileSync('src/GameServerControl.Agent/Servers/BackupService.cs', 'utf8');
const publication = fs.readFileSync('src/GameServerControl.Agent/Servers/BackupArchivePublication.cs', 'utf8');
const orchestrator = fs.readFileSync('src/GameServerControl.Agent/Servers/ServerOrchestrator.cs', 'utf8');
const hv = fs.readFileSync('src/GameServerControl.Agent/Hyperv/HypervService.cs', 'utf8');
const program = fs.readFileSync('src/GameServerControl.Agent/Program.cs', 'utf8');

function requireToken(source, token, message) {
  if (!source.includes(token)) throw new Error(`${message}: missing ${token}`);
}

requireToken(backup, 'BackupMetadata', 'BareMetal backups must persist metadata');
requireToken(backup, 'EffectiveBackupRetention', 'Backups must support per-server retention');
requireToken(backup, 'WriteBackupMetadataAsync', 'Backup metadata must be written after successful archives');
requireToken(backup, 'BackupSafety.ValidateBackupFileName', 'Backup archive names must be validated');
requireToken(backup, 'BackupSafety.NormalizeArchiveEntry', 'ZIP entries must be validated before restore');
requireToken(backup, 'RestoreBareMetalFromArchiveAsync', 'BareMetal restore must use staged restore orchestration');
requireToken(backup, 'Safety backup failed', 'Restore must abort when the mandatory safety backup fails');
requireToken(backup, 'Backup is incomplete', 'Backups that skipped files must be rejected for restore');
requireToken(backup, 'Restorable: HasTrustedArchiveMetadata(meta)', 'Backup listings must reject unverified archives');
requireToken(backup, 'metadata is { FileCount: >= 0, SkippedFiles: 0 }', 'Only complete metadata may authorize restore');
requireToken(backup, 'BackupArchivePublication.PublishAsync', 'Archive bytes must be staged before publication');
requireToken(publication, 'File.Move(stagingPath, archivePath)', 'Completed archives must be published by rename');
requireToken(publication, 'ct.ThrowIfCancellationRequested()', 'Cancellation must be checked before archive publication');
requireToken(publication, 'TryDelete(stagingPath)', 'Interrupted archives must remove their staging files');
requireToken(backup, '.gsc-restore-', 'Restore must extract into staging directories');
requireToken(backup, '.gsc-old-', 'Restore must retain old directories for rollback during swap');
requireToken(backup, 'BackupKind.SafetyArchive', 'Safety archives must be classified separately');
requireToken(backup, 'BackupKind.HyperVCheckpoint', 'Hyper-V checkpoints must be surfaced as backups');

requireToken(hv, 'RestoreCheckpointAsync', 'Hyper-V checkpoint restore primitive is required');
requireToken(hv, 'DeleteCheckpointAsync', 'Hyper-V checkpoint delete primitive is required');

requireToken(program, '"/servers/{id}/backups/settings"', 'Backup settings API is required');
requireToken(program, 'BackupCreateRequest', 'Named backup creation API is required');
requireToken(program, 'MapPost("/servers/{id}/backups"', 'Named backup route is required');

// The former ServerOrchestrator ZIP/restore implementations were less strict
// than BackupService. Keep public compatibility shims, never a second backup
// implementation that can bypass metadata, ownership or staging validation.
for (const [pattern, label] of [
  [/public Task<ActionResult> BackupAsync\(string id, CancellationToken ct = default\)\s*=> _backups\.CreateAsync\(id, ct\);/, 'Create'],
  [/public Task<List<BackupInfo>> ListBackupsAsync\(string id, CancellationToken ct = default\)\s*=> _backups\.ListAsync\(id, ct\);/, 'List'],
  [/public Task<ActionResult> RestoreBackupAsync\(\s*string id, string backupName, CancellationToken ct = default\)\s*=> _backups\.RestoreAsync\(id, backupName, ct\);/, 'Restore'],
  [/public Task<bool> DeleteBackupAsync\(\s*string id, string backupName, CancellationToken ct = default\)\s*=> _backups\.DeleteAsync\(id, backupName, ct\);/, 'Delete']
]) {
  if (!pattern.test(orchestrator))
    throw new Error('Legacy ' + label + ' backup entry point must delegate to BackupService');
}
for (const disallowed of ['ZipArchive', 'ZipFile', 'BackupBareMetalAsync', 'private string BackupRoot']) {
  if (orchestrator.includes(disallowed))
    throw new Error('ServerOrchestrator must not reimplement backup archive IO: ' + disallowed);
}

console.log('Backup lifecycle contract OK.');
