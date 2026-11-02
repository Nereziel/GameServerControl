import fs from 'node:fs';

const backup = fs.readFileSync('src/GameServerControl.Agent/Servers/BackupService.cs', 'utf8');
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
requireToken(backup, 'Restorable: meta?.SkippedFiles is not > 0', 'Incomplete backups must be marked non-restorable in listings');
requireToken(backup, '.gsc-restore-', 'Restore must extract into staging directories');
requireToken(backup, '.gsc-old-', 'Restore must retain old directories for rollback during swap');
requireToken(backup, 'BackupKind.SafetyArchive', 'Safety archives must be classified separately');
requireToken(backup, 'BackupKind.HyperVCheckpoint', 'Hyper-V checkpoints must be surfaced as backups');

requireToken(hv, 'RestoreCheckpointAsync', 'Hyper-V checkpoint restore primitive is required');
requireToken(hv, 'DeleteCheckpointAsync', 'Hyper-V checkpoint delete primitive is required');

requireToken(program, '"/servers/{id}/backups/settings"', 'Backup settings API is required');
requireToken(program, 'BackupCreateRequest', 'Named backup creation API is required');
requireToken(program, 'MapPost("/servers/{id}/backups"', 'Named backup route is required');

console.log('Backup lifecycle contract OK.');
