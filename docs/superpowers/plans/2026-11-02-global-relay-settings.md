# Global Relay Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move Agent-wide public Relay connection configuration into one global Settings view while leaving per-server exposure and public ports in each server Network workspace.

**Architecture:** `PublicRelayManager` remains the source of truth and persistence owner. New explicit global models/endpoints expose write-only-token-safe settings; the Web UI gains a top-level Settings surface and stops sending global connection fields through per-server saves. Existing server endpoints remain backward compatible.

**Tech Stack:** .NET 8 minimal APIs, existing `PublicRelayManager`, browser JavaScript/HTML, Node Web UI contract validation, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-02-global-relay-settings.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Never expose the stored Relay API token in API responses or UI state.
- Preserve `public-relay.json` as the storage location and format owner.
- Do not change VPS Relay routing behavior.
- Keep `/api/servers/{id}/relay` backward compatible.

## Review Focus

- Blank token in global Settings must preserve the currently stored token.
- Clearing TargetAddress must restore Tailscale auto-detection.
- Clearing Relay URL or token must produce an unconfigured status without exposing secrets.
- Per-server saves must not accidentally overwrite global Relay fields.
- Cloud-staged Web UI must contain the same Settings surface as local Agent UI.

---

### Task 1: Contract tests for the new Settings ownership

**Files:**
- Create: `scripts/validate-global-settings.mjs`
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- Consumes: current source files only.
- Produces: CI contract that requires the global Settings API/UI and rejects global Relay fields in per-server UI save logic.

- [ ] Add a static contract test that requires `/api/settings/public-relay` GET/PUT, a top-level `settingsView`, and no `swRelayUrl`/`swRelayToken`/`swRelayTarget`/`swRelayPublicAddress` inputs in `jobs-mods.js`.
- [ ] Run it through CI and verify RED because the feature is not implemented yet.
- [ ] Keep the validator in both Windows and Linux CI through the existing Web UI validation stage.

### Task 2: Global Relay API

**Files:**
- Modify: `src/GameServerControl.Shared/RelayModels.cs`
- Modify: `src/GameServerControl.Agent/Networking/PublicRelayManager.cs`
- Modify: `src/GameServerControl.Agent/Program.cs`

**Interfaces:**
- Produces: `PublicRelayGlobalSettings`, `PublicRelayGlobalStatus`, `PublicRelayManager.GetGlobalStatusAsync()`, `PublicRelayManager.SaveGlobalAsync()` and `/api/settings/public-relay` GET/PUT.

- [ ] Add safe global request/status records; status exposes only `ApiTokenConfigured`.
- [ ] Add manager read/save methods using existing validation and persistence.
- [ ] Preserve null-token-as-keep behavior; blank target/public address clears those optional overrides.
- [ ] Add authenticated GET and admin-only PUT endpoints.
- [ ] Build Agent and verify the new API compiles.

### Task 3: Global Settings UI and per-server simplification

**Files:**
- Modify: `src/GameServerControl.Agent/WebUi/index.html`
- Modify: `src/GameServerControl.Agent/WebUi/jobs-mods.js`

**Interfaces:**
- Consumes: `/api/settings/public-relay`.
- Produces: top-level Settings view plus per-server Relay UI that submits only `enabled` and `publicPorts`.

- [ ] Add top-level Settings tab/view and render/save functions.
- [ ] Show Relay configured/reachable status, URL, write-only token field, configured/auto-detected target, public address and explanatory help.
- [ ] Remove global connection inputs from per-server Network relay section and replace unconfigured guidance with a Settings navigation action.
- [ ] Update per-server save payload to contain only `enabled` and `publicPorts`.
- [ ] Run Web UI syntax/staging checks and the new ownership contract.

### Task 4: Whole-build verification

**Files:** none expected.

- [ ] Verify latest HEAD Windows job passes Web UI validation, restore, Release build, publish and artifact upload.
- [ ] Verify latest HEAD Linux job passes Web UI validation, Cloudflare bundle, profile contracts, Agent/Relay builds, publish and artifact upload.
- [ ] Compare the implementation range and confirm only global-settings-related files changed.
