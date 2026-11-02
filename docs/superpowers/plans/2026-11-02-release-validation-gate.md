# Release Validation Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gate every GameServerControl release on the same repository validation contracts that protect `main`.

**Architecture:** Add one Ubuntu validation job to the release workflow and make both platform release-build jobs depend on it. Reuse existing repository validation commands and Cloudflare Worker tests; do not deploy external resources or change release packaging/version stamping.

**Tech Stack:** GitHub Actions YAML, .NET 8, Node.js, npm, Wrangler dry-run, PowerShell/Bash syntax validation.

**Spec:** `docs/superpowers/specs/2026-11-02-release-validation-gate.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Never use real production secrets in CI validation.
- Do not deploy Cloudflare resources from this workflow.
- Preserve existing release assets and tag-derived version stamping.

## Review Focus

- Manual `workflow_dispatch` release must validate the exact commit it will package.
- Tag-triggered release must validate the tagged commit before platform builds start.
- Placeholder Wrangler secrets must never resemble real credentials or escape the runner temporary directory.
- A validation failure must block both Windows and Linux release-build jobs.
- Existing release asset names and installers must remain unchanged.

---

### Task 1: Add the release validation gate

**Files:**
- Modify: `.github/workflows/release.yml`
- Test: `.github/workflows/build.yml` via the normal push-triggered Build workflow

**Interfaces:**
- Consumes: `prepare.outputs.ref`, existing repository validation scripts/projects, `src/GameServerControl.Cloud/package.json`
- Produces: `validate` job that must succeed before `build-windows` and `build-linux`

- [ ] **Step 1: Establish the failing condition**

Confirm the current release workflow has no `validate` job and both platform build jobs depend only on `prepare`.

- [ ] **Step 2: Add `validate` job**

Use Ubuntu 24.04, checkout `needs.prepare.outputs.ref`, setup .NET 8, then run installer/Relay validation, Web UI/ReadOnly/settings/mod contracts, backup/token/security contracts, backup lifecycle/UI contracts, profile contracts, and Cloudflare Worker `npm test`, `npm run build`, and Wrangler `--dry-run` with placeholder secrets.

- [ ] **Step 3: Gate both release platform builds**

Change both `build-windows` and `build-linux` to `needs: [prepare, validate]` while continuing to use `needs.prepare.outputs.ref` and `needs.prepare.outputs.version`.

- [ ] **Step 4: Verify**

Push the workflow change and require the normal Build workflow to complete successfully on both Windows and Linux, including all security/Worker tests and self-contained publishes.

- [ ] **Step 5: Review release workflow contract**

Fetch `.github/workflows/release.yml` from `main` and confirm version stamping, installer assets, and release asset names are unchanged except for the new validation dependency/job.
