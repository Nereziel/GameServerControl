# ARCH-01 Low-Privilege Runner Design

## Intent

ARCH-01 reduces the blast radius of untrusted or vulnerable Windows game-server and mod code. Today `GameServerControlAgent` runs as LocalSystem and directly starts BareMetal game processes, so those processes inherit LocalSystem. The target architecture keeps privileged host control in the Agent while moving Windows BareMetal game execution into dedicated low-privilege runner services.

Linux is already intentionally unprivileged (`User=gsc`, `NoNewPrivileges=true`) and is not changed by this design. Hyper-V guest process execution is also out of scope.

## Goals

- No Windows BareMetal game process launched by GameServerControl runs as LocalSystem.
- Give each managed Windows BareMetal server its own Windows virtual service account so compromise of one game/mod does not automatically grant the identity of another server.
- Keep firewall, Agent update, SCM provisioning, SteamCMD installation/update, backup/config administration and other host-control operations in the privileged Agent.
- Use a narrow local IPC contract that cannot ask a runner to execute an arbitrary executable or arbitrary shell command.
- Preserve current start/stop/status/console behavior as far as practical.
- Preserve existing Satisfactory data that currently lives in the LocalSystem profile.
- Preserve release integrity and rollback behavior for the new Runner payload.

## Non-goals

- Moving Linux process execution to a separate service.
- Moving Hyper-V guest execution to the Runner.
- Making SteamCMD installation/update low privilege in this iteration.
- Replacing all privileged Agent filesystem operations.
- Reattaching redirected stdin/stdout after a Runner process itself is restarted; this is no worse than the current Agent-restart limitation.
- Using Runner IPC over TCP, HTTP or any remotely reachable transport.

## Anti-goals

- A shared Runner identity for all game servers.
- Granting any Runner account read access to Agent token/config state.
- Accepting executable paths, working directories or launch arguments from IPC commands.
- Falling back silently to direct LocalSystem `Process.Start` when Runner provisioning or IPC fails.
- Keeping legacy Scheduled Task game launch as a privileged bypass around the Runner.

## Current State

On Windows, `GameServerControlAgent` is registered without an explicit service account, so SCM runs it as LocalSystem. `LocalProcessService.StartProcessAsync` directly uses `Process.Start`, including redirected stdin/stdout for interactive servers. BareMetal orchestration may also start a configured Scheduled Task. Both paths can therefore execute game/mod code with privileges outside the intended game-server boundary.

On Linux, the Agent systemd unit already runs as `gsc` with `NoNewPrivileges=true` and a restricted writable-path set, so the original ARCH-01 risk does not apply in the same way.

## Approaches Considered

### 1. Restricted child token inside the Agent

The Agent could create a restricted token and spawn the game with Win32 APIs. This is the smallest change, but the privileged Agent still owns the child-process boundary directly, token construction is subtle, and there is no independent service identity or clean server-to-server ACL identity. Rejected.

### 2. One shared low-privilege Runner service

A single Runner under LocalService or one virtual account would remove LocalSystem from game processes, but every game/mod would share the same Windows identity and therefore any filesystem grants needed by one server would be available to all compromised servers. Rejected for the final ARCH-01 design.

### 3. One Runner service and virtual account per managed server

Chosen. The Agent provisions one deterministic Windows service per BareMetal server. Each service uses its own virtual account `NT SERVICE\<service-name>`, a per-server manifest and a per-server named pipe. This removes LocalSystem from game/mod runtime while giving the OS a distinct SID for each server.

## Process Boundary

### Privileged Control Service

`GameServerControlAgent` remains LocalSystem on Windows. It owns:

- Windows Service Control Manager operations for Runner provisioning.
- Windows Firewall changes.
- Agent/Runner release update and rollback.
- SteamCMD install/update in this iteration.
- Existing privileged config, backup and host-management operations.
- Runner manifest creation and ACL reconciliation.
- Browser/API authentication and authorization.

The Agent does not directly start a Windows BareMetal game executable after this change.

### Per-server Runner

A new `GameServerControl.Runner` worker service is installed once as a hardened binary payload. For each managed Windows BareMetal server, the Agent creates a distinct service whose binary path points to that common Runner executable plus a per-server manifest path.

The service name, pipe name and state directory are derived from a deterministic SHA-256 identifier of the server ID. User-provided server IDs are never embedded directly into SCM names or pipe names.

The service logon identity is the Windows virtual account matching the service name. No password is stored.

## Runner Manifest

Each Runner gets one immutable-at-launch manifest written by the Agent to a protected per-server state directory. It contains only information needed by that game runtime:

- server ID,
- executable path,
- working directory,
- argument array,
- whether redirected stdin/stdout is required,
- Steam App ID when needed for game-specific graceful stop behavior,
- bounded list of additional process names for that server,
- autostart intent.

The manifest may contain a game password already present in game launch arguments. This is acceptable for the Runner's own SID because the same game can observe its own process command line. Other Runner identities and ordinary local users must not be granted access.

The Runner validates that executable and working-directory paths are absolute and rejects malformed manifests before opening its pipe or starting a game.

## Narrow IPC Protocol

Transport is a Windows named pipe. The Runner creates a pipe ACL that permits only LocalSystem and local Administrators to connect. There is no network listener and no bearer token transported over the pipe.

Each connection carries one bounded JSON request and one bounded JSON response. Commands are an enum-like operation plus operation-specific data; launch metadata is never supplied by the request.

Supported operations:

- `Ping`
- `Start`
- `Find`
- `Stop`
- `ConsoleState`
- `SendConsoleInput`
- `KillExtras`

`Start` always reads executable, working directory and arguments from the Runner manifest. `KillExtras` always uses the manifest's allowlisted additional process names. `SendConsoleInput` remains limited to one command, max 4096 characters, with no CR/LF.

Requests and responses have explicit maximum serialized sizes. Unknown commands and malformed requests fail closed.

## Process Lifecycle

The Runner owns the game `Process` handle when it launches a process. It preserves current launch behavior, including shell execution for non-interactive Windows launchers such as `.bat` files and redirected stdio for stdin-console profiles.

`Find` can rediscover a process by expected executable path after a Runner restart. This supports status/stop of a still-running game without requiring the Runner to remain continuously attached.

Graceful stop logic that must execute in the game identity/process context moves into the Runner, including Valheim console interrupt handling and process-tree termination. The privileged Agent no longer performs game-process kill by arbitrary name; `KillExtras` executes inside the same per-server Runner identity and only for manifest-listed names.

The Agent retains firewall ownership. It opens rules before asking the Runner to start and closes them when stop succeeds or status observes the process has exited.

## Console Behavior

For stdin-console profiles, the Runner retains redirected stdin/stdout and a bounded in-memory line buffer with monotonically increasing sequence numbers.

The Agent polls `ConsoleState` and forwards only unseen lines to the existing SignalR `server:<id>` group. `StatusHub.SubscribeConsole` reads initial Runner console state asynchronously. `SendConsoleInput` proxies through the Runner.

If the Runner service restarts while a game continues, process discovery still works but redirected stdin/stdout cannot be reattached. The UI must report input unavailable rather than claiming an attached console.

## Windows ACL Model

The Runner payload directory remains owned/writable only by SYSTEM and local Administrators. Individual Runner service SIDs receive read/execute only.

Each per-server Runner state directory is writable by SYSTEM/Administrators and readable by only that server's virtual service SID.

The Agent grants that server SID Modify permission only to the managed game working directory and explicitly configured save directories needed by the process. It does not grant access to the Agent install/state directory.

ACL reconciliation is idempotent. Failure to establish the required ACLs is a hard provisioning/start failure; the Agent must not fall back to LocalSystem execution.

## Legacy Scheduled Tasks and Autostart

Windows BareMetal game launch no longer uses Scheduled Tasks after Runner provisioning.

If a server has a legacy `ScheduledTaskName`, the Agent reads its enabled state once, maps that to Runner autostart, then disables/ends the legacy task so it cannot launch a privileged duplicate process.

The existing autostart API becomes Runner-backed for Windows BareMetal servers. Runner service startup type is `auto` when autostart is enabled and `demand` otherwise. Linux and Hyper-V behavior remain unchanged.

## Satisfactory Compatibility

The current Satisfactory profile stores saves under the LocalSystem profile. Changing process identity without migration would make those existing saves appear missing.

For Steam App ID `1690800`, provisioning preserves compatibility by creating the Runner profile's `LocalAppData\FactoryGame` path as a junction to the existing SystemProfile `FactoryGame` directory. The Agent grants only the corresponding Runner service SID Modify access to that legacy target. The existing backup path therefore remains valid and current saves are not silently relocated.

No generic profile junction behavior is introduced for other games.

## Release and Update

Windows releases add a separate self-contained `GameServerControl-Runner-windows.zip` asset. It is included in `SHA256SUMS` and verified by the installer before use.

The installer places the Runner payload in `C:\GameServerControl\Runner` with restrictive ACLs. During an Agent/Runner update it stops existing `GameServerControlRunner_*` services, preserves `Runner.previous`, replaces the verified Runner payload, then restarts only Runner services that had been running.

Rollback preserves Agent and Runner payloads as a unit. A failed Agent health gate restores both previous payloads before services are restarted.

## Failure Semantics

- Runner binary missing: start fails closed with a clear error.
- Runner service provisioning/ACL failure: start fails closed; no direct process fallback.
- Pipe unavailable/service failed: operation returns an error; Agent may retry a bounded number of times after starting the service.
- Manifest invalid: Runner exits/fails closed.
- Unknown server: no service or manifest is provisioned.
- Legacy scheduled task migration failure: provisioning fails rather than leaving both launch paths active.

## Testing Requirements

Persistent CI contracts must prove:

- Windows game launch no longer directly calls `Process.Start` from `LocalProcessService`.
- BareMetal orchestration has no Scheduled Task launch bypass.
- Runner identities and pipe names are deterministic and distinct across server IDs.
- IPC requests cannot carry executable paths/arguments.
- Runner manifest validation rejects non-rooted executable/working paths.
- Runner pipe ACL permits only SYSTEM and Administrators.
- Installer/release produce, hash and rollback the Runner asset.
- Existing Linux build and behavior remain green.
- Windows Agent, Runner and Tray self-contained publishes all succeed.
