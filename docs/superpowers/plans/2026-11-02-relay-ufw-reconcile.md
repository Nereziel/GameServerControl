# Relay UFW Rule Reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reconcile the Relay API UFW rule across installer reruns so stale Tailscale IP/interface/port rules are removed safely.

**Architecture:** Add a focused shell helper used by `deploy/relay/install.sh`. The installer reads the previous Relay bind/interface before replacing the env file, then asks the helper to delete only GameServerControl-owned rules and add the current one when UFW is active. CI exercises the helper with a fake `ufw` command and packages it with Relay releases.

**Tech Stack:** Bash, UFW CLI, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-02-relay-ufw-reconcile.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Never expose the Relay API on the public interface.
- Never delete unrelated UFW rules.
- Preserve existing Relay token/state update behavior.
- Keep the one-command release installer working.

## Review Focus

- Previous Tailscale IP differs from current IP.
- Previous API port or Tailscale interface differs from current values.
- Re-running with unchanged values must not duplicate rules.
- UFW installed but inactive must remove stale managed rules without adding a new one.
- Missing previous env data must not fail installation.

---

### Task 1: Regression test for UFW reconciliation

**Files:**
- Create: `scripts/test-relay-ufw-reconcile.sh`
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- Consumes: future `deploy/relay/ufw-reconcile.sh`
- Produces: CI behavior contract for `gsc_reconcile_relay_api_ufw`

- [ ] Write a failing shell test that sources the helper and stubs `ufw`.
- [ ] Verify RED because the helper does not exist yet.
- [ ] Add the test to Linux CI.

### Task 2: Add UFW reconciliation helper and installer integration

**Files:**
- Create: `deploy/relay/ufw-reconcile.sh`
- Modify: `deploy/relay/install.sh`

**Interfaces:**
- Produces: `gsc_reconcile_relay_api_ufw current_iface current_ip current_port previous_iface previous_ip previous_port`

- [ ] Implement exact managed-rule deletion using comment `GameServerControl Relay API`.
- [ ] Read previous bind/interface before overwriting `/etc/gameservercontrol-relay.env`.
- [ ] Call reconciliation after current Tailscale settings are resolved.
- [ ] Verify GREEN regression test and `bash -n`.

### Task 3: Package helper in build and release artifacts

**Files:**
- Modify: `.github/workflows/build.yml`
- Modify: `.github/workflows/release.yml`

- [ ] Copy `ufw-reconcile.sh` into Relay publish output.
- [ ] Validate helper syntax in CI/release workflows.
- [ ] Run full Build workflow and confirm Windows + Linux jobs succeed.
