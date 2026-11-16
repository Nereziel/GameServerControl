# Installation jobs, cancellation and Minecraft recovery

This page describes the Phase B installation recovery behavior. It applies to
Agent versions that advertise the relevant feature names through the authenticated
`GET /api/capabilities` endpoint.

## Installation history

- Open **Installations** after selecting the relevant computer. Admin users can
  see the Agent's retained job summaries even after browser refresh or Agent restart.
- The Agent persists up to **100** job summaries in its configured data directory
  (default file: `install-jobs.json`; override: `Agent:InstallJobsPath`).
  Installer stdout and full log lines are **not** persisted in this journal.
- If an Agent restarts while an install is running, its previous job is marked
  **Interrupted** and unsuccessful. **This is not automatic resume.**
  Inspect the installation folder before choosing a new installation or recovery.
- The Web UI shows at most **30** installation cards per selected computer.
  **Clear finished** hides terminal jobs in this browser for that computer;
  it does not delete the Agent's audit/history data. Another browser may still
  display those jobs.
- The history and cancellation controls appear only if the selected Agent
  advertises `install.history` and `install.cancel`. Older Agents continue
  to work with their previous browser-local job tracking. A Cloudflare-managed
  computer uses that computer's capabilities rather than another host's.

## Cancelling an installation

An Admin may select **Cancel install** on an unfinished job. The endpoint
`POST /api/servers/install/{jobId}/cancel` accepts the cancellation request;
the job changes to a terminal state when the ongoing operation observes it.

**Cancellation is cooperative and does not roll back game data.** Check for
partially downloaded or already-installed files before retrying. Do not delete
an existing Minecraft world to resolve an interrupted install.

The Admin-only `GET /api/servers/install/jobs` endpoint lists retained
summaries. An individual job remains available at
`GET /api/servers/install/{jobId}`. Do not paste API tokens into issue
reports, screenshots, or troubleshooting logs.

## Recovering a previously downloaded Minecraft server

Use this when the Minecraft download succeeded but registration failed:

1. Check the existing installation folder and back up any important worlds.
   Do not run a fresh install over that folder.
2. In the UI, select the right computer, then **Add existing**.
3. Choose the relevant Minecraft profile; enter a unique lowercase server ID,
   a name, and the **absolute** path to the existing installation.
4. Choose **Recover Minecraft installation** and confirm.

The Agent reads the installation marker `.gsc-minecraft.json` and validates
the distribution without re-downloading, replacing, or moving the installed
files. For Java it requires `server.jar` and an already accepted
`eula=true` file. For official Bedrock it requires the expected executable.
The recovery operation creates the missing server registry entry; it does not
change the world's files or accept licenses on the user's behalf.

The Admin-only endpoint is `POST /api/servers/minecraft/recover` with
`serverId`, `name`, `installPath`, and optional `memoryMb`.
A folder that lacks valid GSC metadata cannot be safely recovered through
this shortcut; use the standard, explicit Add existing workflow only after
checking the executable, arguments, save paths and configuration.

## Java compatibility and diagnostics

The Minecraft installer probes `java -version` before downloading a Java
server. It checks known Minecraft version requirements (for example, Java 17
for Minecraft 1.18–1.20.4, Java 21 for 1.20.5 and newer). It does not install
or update Java. Older or unrecognized version combinations may require
manual compatibility verification.

When recovery or installation fails, review the job status and Agent logs,
the installation folder and filesystem permissions. Confirm free disk space,
selected edition, accepted EULA/license, available Java version and conflicting
server IDs before retrying.

**Deployment limitation:** These changes do not alter Windows Agent directory
ACLs or Runner isolation. Windows ACL issue #55 requires a separate on-host
elevated and non-elevated verification before deployment. No production
Agent, Cloudflare Worker or VPS is updated by this documentation.
