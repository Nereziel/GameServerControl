# Backup Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add named backups, per-server retention, complete Hyper-V checkpoint handling, and traversal-safe staged restore in the server Backups workspace.

**Architecture:** Preserve the current Agent-owned backup model. Extend shared backup contracts and `ServerDef`, harden `ServerOrchestrator` restore around a reusable path-safety helper, add missing Hyper-V checkpoint restore/delete primitives, and update the existing Web UI tab without introducing browser-side archive transfer.

**Tech Stack:** .NET 8 / ASP.NET Core, System.IO.Compression, Hyper-V PowerShell, vanilla JavaScript Web UI, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-02-backup-management.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Keep the existing `/api/servers/{id}/backup`, list, restore and delete routes backward compatible.
- Do not send backup archive bytes through Cloudflare/browser for normal create/restore/delete flows.
- Retention defaults to `Agent:BackupRetention`, then 10.
- Restore must never proceed after a failed pre-restore safety copy.
- Never manage Hyper-V checkpoints that were not created by GameServerControl.

## Review Focus

- Encoded/alternate-separator traversal in backup names and ZIP entries must be rejected.
- Duplicate or missing save-directory prefixes must fail before current data changes.
- A restore swap failure must leave recoverable original save directories.
- Legacy ZIPs without sidecar metadata must still list and restore.
- Retention must not count or prune pre-restore safety copies as ordinary backups.

---

### Task 1: Shared backup contracts and safety tests

**Files:**
- Modify: `src/GameServerControl.Shared/Models.cs`
- Create: `src/GameServerControl.Shared/BackupSafety.cs`
- Create: `tools/GameServerControl.BackupCheck/GameServerControl.BackupCheck.csproj`
- Create: `tools/GameServerControl.BackupCheck/Program.cs`
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- Produces: `BackupKind`, extended `BackupInfo`, `BackupCreateRequest`, `BackupSettings`, optional `ServerDef.BackupRetention`, and `BackupSafety` filename/entry/path validation helpers.

- [ ] **Step 1: Write the failing backup safety contract tool** covering valid relative entries, `..`, rooted paths, alternate separators and unsafe backup file names.
- [ ] **Step 2: Wire the tool into both CI platforms and verify RED** because the new shared contracts/helpers do not exist yet.
- [ ] **Step 3: Implement the shared contracts/helpers.**
- [ ] **Step 4: Verify the contract tool and Shared build pass.**
- [ ] **Step 5: Commit.**

### Task 2: Agent backup lifecycle hardening

**Files:**
- Modify: `src/GameServerControl.Agent/Servers/ServerOrchestrator.cs`
- Modify: `src/GameServerControl.Agent/Hyperv/HypervService.cs`
- Modify: `src/GameServerControl.Agent/Program.cs`

**Interfaces:**
- Consumes: Task 1 backup contracts/safety helpers.
- Produces: named archive creation, sidecar metadata, per-server retention API, safe staged BareMetal restore, GameServerControl-owned Hyper-V checkpoint list/restore/delete and mandatory pre-restore safety copies.

- [ ] **Step 1: Extend the contract tool/source validation so current Agent code fails the required lifecycle contract.**
- [ ] **Step 2: Verify RED.**
- [ ] **Step 3: Implement the lifecycle/API changes with backward-compatible routes.**
- [ ] **Step 4: Run Agent build and backup contract checks.**
- [ ] **Step 5: Commit.**

### Task 3: Backups Web UI

**Files:**
- Modify: `src/GameServerControl.Agent/WebUi/server-workspace.js`
- Create: `scripts/validate-backup-ui.mjs`
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- Consumes: Task 2 list/create/settings/restore/delete APIs.
- Produces: named backup input, retention control, metadata cards, restore safety messaging and running-state restore disablement.

- [ ] **Step 1: Add a failing static UI contract for the new controls and API calls.**
- [ ] **Step 2: Verify RED.**
- [ ] **Step 3: Implement the Backups tab UI.**
- [ ] **Step 4: Run Web UI syntax validation, backup UI contract and full builds.**
- [ ] **Step 5: Commit.**

### Task 4: Whole-repository verification

**Files:**
- No production changes unless verification finds a defect.

- [ ] **Step 1: Run/observe the full GitHub Actions Build workflow for the final HEAD.**
- [ ] **Step 2: Confirm Windows and Linux jobs, backup contracts, Web UI validation and publish steps are green.**
- [ ] **Step 3: Review the final diff for path traversal, destructive restore ordering and backward compatibility.**
