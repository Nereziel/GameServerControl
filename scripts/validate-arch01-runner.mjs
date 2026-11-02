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
const client = read(clientPath);
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

requireCheck(client.includes('NamedPipeClientStream'), 'Agent Runner client must use the local named-pipe transport.');
requireCheck(client.includes('RunnerProtocolLimits.MaxRequestBytes') && client.includes('RunnerProtocolLimits.MaxResponseBytes'), 'Agent Runner client must enforce protocol frame limits.');
requireCheck(client.includes('ConnectAsync') && client.includes('CancellationTokenSource'), 'Agent Runner client must use bounded/cancellable pipe connection attempts.');

requireCheck(local.includes('WindowsRunnerServiceManager'), 'LocalProcessService must delegate Windows BareMetal runtime to WindowsRunnerServiceManager.');
requireCheck(local.includes('_windowsRunner.StartAsync(def, ct)'), 'Windows start path must delegate to the Runner.');
requireCheck(/StartLocalProcessAsync[\s\S]{0,500}?OperatingSystem\.IsWindows\(\)[^\n]*throw/m.test(local), 'Direct process launch helper must fail closed on Windows.');
requireCheck(!/StartBareMetalAsync[\s\S]{0,5000}?StartScheduledTaskAsync\s*\(/m.test(orchestrator), 'StartBareMetalAsync still has a Scheduled Task launch bypass.');
requireCheck(local.includes('PumpRunnerConsoleAsync') && local.includes('LastSequence'), 'Runner console must have a sequence-aware live forwarding pump.');
requireCheck(local.includes('StatusHub.ServerGroup(def.Id)') && /SendAsync\s*\(\s*["']logLine["']/.test(local), 'Runner console pump must publish new lines only to the selected server SignalR group.');
requireCheck(manager.includes('PollConsoleStateAsync') && manager.includes('_client.SendAsync'), 'Live console polling must use the existing local pipe without repeating Runner provisioning.');

requireCheck(manager.includes('NT SERVICE\\'), 'Runner services must use Windows virtual service accounts.');
requireCheck(manager.includes('ArgumentList'), 'Runner provisioning commands must use ProcessStartInfo.ArgumentList instead of shell interpolation.');
requireCheck(!manager.includes('cmd.exe') && !manager.includes('powershell.exe'), 'Runner service provisioning must not shell through cmd.exe or powershell.exe.');
requireCheck(manager.includes('RunnerManifest') && manager.includes('JsonSerializer'), 'Agent must persist manifest-defined launch metadata instead of sending it over IPC.');
requireCheck(manager.includes('icacls.exe') && manager.includes('(OI)(CI)(M)') && manager.includes('(OI)(CI)(RX)'), 'Agent must grant per-server Modify access to game roots and read/execute access to Runner payload.');
requireCheck(manager.includes('schtasks.exe') && manager.includes('/disable') && manager.includes('/end'), 'Legacy Scheduled Tasks must be disabled and ended during Runner migration.');
requireCheck(manager.includes('1690800') && manager.includes('FactoryGame') && manager.includes('CreateSymbolicLink'), 'Satisfactory must preserve its existing SystemProfile data through an idempotent directory link.');
requireCheck(manager.includes('SetAutostartAsync') && manager.includes('GetAutostartAsync'), 'Runner manager must own Windows BareMetal autostart state.');

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
