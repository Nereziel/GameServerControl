import fs from 'node:fs';

const errors = [];
const read = path => fs.existsSync(path) ? fs.readFileSync(path, 'utf8') : '';
const requireCheck = (condition, message) => { if (!condition) errors.push(`ARCH-01: ${message}`); };

const runnerProject = 'src/GameServerControl.Runner/GameServerControl.Runner.csproj';
const runnerProgramPath = 'src/GameServerControl.Runner/Program.cs';
const runnerHostPath = 'src/GameServerControl.Runner/RunnerProcessHost.cs';
const runnerPipePath = 'src/GameServerControl.Runner/RunnerPipeServer.cs';
const managerPath = 'src/GameServerControl.Agent/Hyperv/WindowsRunnerServiceManager.cs';
const clientPath = 'src/GameServerControl.Agent/Hyperv/WindowsRunnerPipeClient.cs';
const localPath = 'src/GameServerControl.Agent/Hyperv/LocalProcessService.cs';
const orchestratorPath = 'src/GameServerControl.Agent/Servers/ServerOrchestrator.cs';
const installerPath = 'deploy/windows/install.ps1';
const buildPath = '.github/workflows/build.yml';
const releasePath = '.github/workflows/release.yml';

requireCheck(fs.existsSync(runnerProject), 'missing GameServerControl.Runner project.');
requireCheck(fs.existsSync(runnerProgramPath), 'missing Runner service host.');
requireCheck(fs.existsSync(runnerHostPath), 'missing RunnerProcessHost.');
requireCheck(fs.existsSync(runnerPipePath), 'missing RunnerPipeServer.');
requireCheck(fs.existsSync(managerPath), 'missing WindowsRunnerServiceManager.');
requireCheck(fs.existsSync(clientPath), 'missing WindowsRunnerPipeClient.');

const runnerProgram = read(runnerProgramPath);
const runnerHost = read(runnerHostPath);
const runnerPipe = read(runnerPipePath);
const local = read(localPath);
const orchestrator = read(orchestratorPath);
const manager = read(managerPath);
const installer = read(installerPath);
const build = read(buildPath);
const release = read(releasePath);

requireCheck(runnerProgram.includes('--manifest') && runnerProgram.includes('RunnerManifest'), 'Runner must load and validate a per-server manifest before hosting.');
requireCheck(/StartAsync\s*\(\s*CancellationToken/.test(runnerHost), 'RunnerProcessHost.StartAsync must not accept executable/path/argument launch metadata.');
requireCheck(!/StartAsync\s*\([^)]*(?:string|IEnumerable|\[\])[^)]*\)/m.test(runnerHost), 'Runner start API exposed caller-supplied launch metadata.');
requireCheck(runnerHost.includes('RunnerProtocolLimits.MaxConsoleInputChars'), 'Runner console input must enforce the shared protocol bound.');
requireCheck(runnerHost.includes('StopProcessNames'), 'Runner extra-process termination must come from its manifest allowlist.');
requireCheck(runnerPipe.includes('NamedPipeServerStream'), 'Runner IPC must use a local named pipe.');
requireCheck(runnerPipe.includes('PipeSecurity') && runnerPipe.includes('WellKnownSidType.LocalSystemSid') && runnerPipe.includes('WellKnownSidType.BuiltinAdministratorsSid'), 'Runner pipe ACL must explicitly authorize only SYSTEM and local Administrators.');
requireCheck(runnerPipe.includes('RunnerProtocolLimits.MaxRequestBytes') && runnerPipe.includes('RunnerProtocolLimits.MaxResponseBytes'), 'Runner pipe must enforce bounded request and response sizes.');

requireCheck(local.includes('WindowsRunnerServiceManager'), 'LocalProcessService must delegate Windows BareMetal runtime to WindowsRunnerServiceManager.');
requireCheck(!/OperatingSystem\.IsWindows\(\)[\s\S]{0,2200}?Process\.Start\s*\(/m.test(local), 'Windows LocalProcessService still has a direct game Process.Start fallback.');
requireCheck(!/StartBareMetalAsync[\s\S]{0,5000}?StartScheduledTaskAsync\s*\(/m.test(orchestrator), 'StartBareMetalAsync still has a Scheduled Task launch bypass.');

requireCheck(manager.includes('NT SERVICE\\'), 'Runner services must use Windows virtual service accounts.');
requireCheck(manager.includes('ArgumentList'), 'Runner provisioning commands must use ProcessStartInfo.ArgumentList instead of shell interpolation.');
requireCheck(!manager.includes('cmd.exe') && !manager.includes('powershell.exe'), 'Runner service provisioning must not shell through cmd.exe or powershell.exe.');
requireCheck(manager.includes('GameServerControlRunner_'), 'Runner service names must use the deterministic GameServerControlRunner_ prefix.');

requireCheck(build.includes('GameServerControl.Runner'), 'Build workflow must build/publish the Windows Runner.');
requireCheck(release.includes('GameServerControl.Runner'), 'Release workflow must publish the Windows Runner.');
requireCheck(release.includes('GameServerControl-Runner-windows.zip'), 'Release workflow must create the Runner Windows asset.');
requireCheck(release.includes('SHA256SUMS') && release.includes('GameServerControl-Runner-windows.zip'), 'Runner asset must be covered by the release SHA256 manifest.');

requireCheck(installer.includes('GameServerControl-Runner-windows.zip'), 'Windows installer must download the Runner asset from the resolved release.');
requireCheck(installer.includes('Runner.previous'), 'Windows installer must retain a Runner.previous rollback payload.');
requireCheck(installer.includes('GameServerControlRunner_'), 'Windows installer must manage existing Runner services during replacement.');
requireCheck(/Assert-ReleaseAssetHash[\s\S]{0,900}?Runner/m.test(installer) || /Runner[\s\S]{0,900}?Assert-ReleaseAssetHash/m.test(installer), 'Windows installer must hash-verify the Runner asset.');

if (errors.length) {
  for (const error of errors) console.error(error);
  process.exit(1);
}

console.log('ARCH-01 Windows Runner architecture contract OK.');
