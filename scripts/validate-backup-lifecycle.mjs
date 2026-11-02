import fs from 'node:fs';

const orch = fs.readFileSync('src/GameServerControl.Agent/Servers/ServerOrchestrator.cs', 'utf8');
const hv = fs.readFileSync('src/GameServerControl.Agent/Hyperv/HypervService.cs', 'utf8');
const program = fs.readFileSync('src/GameServerControl.Agent/Program.cs', 'utf8');

function requireToken(source, token, message) {
  if (!source.includes(token)) throw new Error(`${message}: missing ${token}`);
}

requireToken(orch, 'BackupMetadata', 'BareMetal backups must persist metadata');
requireToken(orch, 'EffectiveBackupRetention', 'Backups must support per-server retention');
requireToken(orch, 'WriteBackupMetadataAsync', 'Backup metadata must be written after successful archives');
requireToken(orch, 'BackupSafety.ValidateBackupFileName', 'Backup archive names must be validated');
requireToken(orch, 'BackupSafety.NormalizeArchiveEntry', 'ZIP entries must be validated before restore');
requireToken(orch, 'RestoreBareMetalFromArchiveAsync', 'BareMetal restore must use staged restore orchestration');
requireToken(orch, 'Safety backup failed', 'Restore must abort when the mandatory safety backup fails');
requireToken(orch, '.gsc-restore-', 'Restore must extract into staging directories');
requireToken(orch, '.gsc-old-', 'Restore must retain old directories for rollback during swap');
requireToken(orch, 'BackupKind.SafetyArchive', 'Safety archives must be classified separately');
requireToken(orch, 'BackupKind.HyperVCheckpoint', 'Hyper-V checkpoints must be surfaced as backups');

requireToken(hv, 'RestoreCheckpointAsync', 'Hyper-V checkpoint restore primitive is required');
requireToken(hv, 'DeleteCheckpointAsync', 'Hyper-V checkpoint delete primitive is required');

requireToken(program, '"/servers/{id}/backups/settings"', 'Backup settings API is required');
requireToken(program, 'BackupCreateRequest', 'Named backup creation API is required');
requireToken(program, 'MapPost("/servers/{id}/backups"', 'Named backup route is required');

console.log('Backup lifecycle contract OK.');
