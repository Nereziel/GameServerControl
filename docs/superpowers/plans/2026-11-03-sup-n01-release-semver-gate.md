# SUP-N01 Release SemVer Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make release publication fail closed unless the requested release tag exactly matches the repository's stable SemVer source of truth.

**Architecture:** Keep `scripts/version.mjs` as the existing version authority. Add a contract assertion to the release-workflow validator first, observe it fail against the current workflow, then add the exact tag check inside the Release workflow's existing `validate` job so both platform build jobs inherit the gate.

**Tech Stack:** GitHub Actions YAML, Node.js validation scripts.

**Spec:** `docs/superpowers/specs/2026-11-03-sup-n01-release-semver-gate.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Stable SemVer only; no prerelease support changes.
- Do not change release asset names or platform build topology.
- Do not rely on the standalone Version contract as the publication gate.

## Review Focus

- `workflow_dispatch` with a syntactically valid but mismatched tag must fail in `validate` before platform builds.
- Tag-push releases must use the exact pushed tag in the same check.
- The validator must bind the check to `needs.prepare.outputs.tag`, not an unrelated environment value.
- Existing `build-windows` and `build-linux` dependencies on `validate` must remain intact.
- Correct matching tags must continue through validation.

---

### Task 1: Enforce the release SemVer gate contract

**Files:**
- Modify: `scripts/validate-release-workflow.mjs`
- Modify: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: `scripts/version.mjs check <tag>` and `needs.prepare.outputs.tag`.
- Produces: a release validation step that fails on repository/tag mismatch and a static contract that requires it.

- [ ] **Step 1: Write the failing contract assertion**

Require the release workflow to contain the exact command `node scripts/version.mjs check "${{ needs.prepare.outputs.tag }}"` in the validation gate.

- [ ] **Step 2: Run the release-workflow validator and verify RED**

Run: `node scripts/validate-release-workflow.mjs`
Expected: FAIL because the Release workflow does not yet contain the required SemVer gate.

- [ ] **Step 3: Add the minimal release validation step**

In `.github/workflows/release.yml`, immediately after checkout in `validate`, run the exact repository/tag check using `needs.prepare.outputs.tag`.

- [ ] **Step 4: Verify GREEN**

Run: `node scripts/validate-release-workflow.mjs`
Expected: PASS with `Release validation gate contract OK.`

- [ ] **Step 5: Run repository CI**

Expected: Build and all PR contract workflows pass; no release asset or platform dependency regression.
