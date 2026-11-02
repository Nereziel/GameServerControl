# Mod / Plugin Management Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-mod configuration access and make installed/update workflows more resilient while reusing the safe config-file API.

**Architecture:** Keep provider managers and install APIs unchanged. Add UI orchestration in `jobs-mods.js` that discovers allowed config files, ranks likely files for a selected mod, edits them through the existing SHA-protected file API, and improves source/restart/update-all behavior. Add a static contract test to CI so the behavior cannot silently regress.

**Tech Stack:** Browser JavaScript, existing ASP.NET Core Agent APIs, Node-based CI contract checks.

**Spec:** `docs/superpowers/specs/2026-11-02-mod-plugin-management.md`

## Global Constraints
- Work only in `Nereziel/GameServerControl`.
- No unrestricted filesystem endpoint.
- Agent remains responsible for downloading mod archives.
- Cloudflare WebUI must reuse the same assets and API paths.

## Review Focus
- Selected mod has no obvious filename match: fallback must still show all mod/plugin configs.
- Config write conflict: preserve existing SHA conflict behavior and surface the error.
- Running server: operation succeeds but UI warns restart may be required; no automatic restart.
- One update fails during Update all: later updates still run and failures are summarized.
- Source is not HTTP(S): do not render an unsafe external link.

---

### Task 1: Add regression contract

**Files:**
- Create: `scripts/validate-mod-management.mjs`
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- Consumes: `src/GameServerControl.Agent/WebUi/jobs-mods.js`
- Produces: CI assertions for Configure, file API usage, safe Source link, restart warning, and resilient bulk update.

- [ ] Write the contract and add it to Windows + Linux CI.
- [ ] Run CI and confirm RED against the current UI.

### Task 2: Implement per-mod configuration workflow

**Files:**
- Modify: `src/GameServerControl.Agent/WebUi/jobs-mods.js`

**Interfaces:**
- Consumes: `/api/servers/{id}/files`, `/files/content`, `/files/validate`, `/files/history`, existing `api()` and modal helpers.
- Produces: `configureInstalledMod`, config candidate ranking, raw editor save/validation/history entry points.

- [ ] Add Configure to installed cards.
- [ ] Rank likely files by normalized `modId`/`displayName`, then expose all `Mods / plugins` files as fallback.
- [ ] Add safe raw editor with SHA-protected save and history/restore access.

### Task 3: Improve installed/update operations

**Files:**
- Modify: `src/GameServerControl.Agent/WebUi/jobs-mods.js`

**Interfaces:**
- Consumes: existing ModInfo `source`, current update/install/remove APIs, `STATUS`.
- Produces: safe Source action, restart-required notice, resilient Update all summary.

- [ ] Show Source only for HTTP(S).
- [ ] Warn after install/update/remove while server is running.
- [ ] Continue Update all after individual failures and show final success/failure summary.
- [ ] Run full Build workflow and verify Windows + Linux success.
