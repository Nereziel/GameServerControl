# SEC-08 Secret At-Rest Protection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove reusable plaintext Agent credentials from persistent state while preserving existing runtime/API behavior and migrating existing installations safely.

**Architecture:** Add one AES-GCM `SecretProtector` backed by a versioned master-key file. Windows wraps the master key with DPAPI LocalMachine; Linux relies on the existing 0700/0600 service-owned state boundary. A configuration secret store migrates sensitive `appsettings.json` leaves into encrypted `secret-state.json`, while server and relay state use the same protector for field-level encryption.

**Tech Stack:** .NET 8, `System.Security.Cryptography` AES-GCM, Windows DPAPI (`System.Security.Cryptography.ProtectedData`), JSON state, GitHub Actions Windows/Linux CI.

**Spec:** `docs/superpowers/specs/2026-11-02-audit-sec08-secret-at-rest.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Never log or expose plaintext secret values during migration/tests.
- Existing browser/API payload shapes stay unchanged.
- Existing managed bearer tokens remain one-way hashed in `tokens.json`.
- Existing plaintext installations migrate automatically.
- Windows and Linux upgrade paths preserve key material.

## Review Focus

- Existing encrypted state with a missing/wrong master key must fail rather than silently regenerate a key.
- AES-GCM ciphertext must be purpose-bound so copying a value to another secret field fails authentication.
- Legacy `servers.json.bak` must not retain plaintext after migration.
- Environment-supplied configuration secrets must not be unexpectedly copied into persistent encrypted state.
- Reinstall/update scripts must preserve both encryption files and their restrictive permissions.

---

### Task 1: Regression contract and RED CI

**Files:**
- Create: `scripts/validate-audit-sec08.mjs`
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- Consumes: current source tree.
- Produces: CI contract requiring the SEC-08 storage architecture and installer preservation.

- [ ] **Step 1:** Add source-level regression checks for `SecretProtector`, configuration migration, server/relay encryption integration, installer preservation, and removal of the plaintext-at-rest known limitation.
- [ ] **Step 2:** Wire the validator into both Windows and Linux jobs.
- [ ] **Step 3:** Open a draft PR and capture a failing CI run caused by the absent SEC-08 implementation.
- [ ] **Step 4:** Commit the RED contract.

### Task 2: Master-key and AES-GCM primitive

**Files:**
- Create: `src/GameServerControl.Agent/Security/SecretProtector.cs`
- Modify: `src/GameServerControl.Agent/GameServerControl.Agent.csproj`
- Modify: `tools/GameServerControl.SecurityCheck/GameServerControl.SecurityCheck.csproj`
- Modify: `tools/GameServerControl.SecurityCheck/Program.cs`

**Interfaces:**
- Produces: `SecretProtector(string baseDirectory)`, `Protect(string plaintext, string purpose)`, `Unprotect(string protectedValue, string purpose)`, `IsProtected(string value)`.

- [ ] **Step 1:** Add behavior tests for round-trip encryption, non-plaintext persisted envelope, tamper rejection, purpose mismatch rejection, Linux 0600 key mode, and Windows DPAPI key-envelope format.
- [ ] **Step 2:** Run SecurityCheck and verify RED.
- [ ] **Step 3:** Implement versioned AES-GCM envelopes and platform key persistence; never regenerate a key when an existing key file is malformed/unreadable.
- [ ] **Step 4:** Run SecurityCheck and verify GREEN.
- [ ] **Step 5:** Commit.

### Task 3: Encrypted configuration state and bootstrap token migration

**Files:**
- Create: `src/GameServerControl.Agent/Security/ConfigurationSecretStore.cs`
- Modify: `src/GameServerControl.Agent/Auth/FirstRunTokenGenerator.cs`
- Modify: `src/GameServerControl.Agent/Program.cs`
- Modify: `tools/GameServerControl.SecurityCheck/GameServerControl.SecurityCheck.csproj`
- Modify: `tools/GameServerControl.SecurityCheck/Program.cs`

**Interfaces:**
- Produces: configuration migration/apply before auth/service consumers; `secret-state.json` encrypted values; first-run token persistence through the encrypted store.

- [ ] **Step 1:** Add behavior tests using temporary `appsettings.json` for ApiToken, CertPassword, provider ApiKey, and guest Password migration plus restart/reload behavior and env override non-persistence.
- [ ] **Step 2:** Verify RED.
- [ ] **Step 3:** Implement sensitive-leaf discovery under `Agent`, encrypted state load/save, appsettings sentinel rewrite, in-memory configuration application, and encrypted first-run token creation.
- [ ] **Step 4:** Verify GREEN and confirm no test fixture plaintext appears in `appsettings.json` or `secret-state.json` after migration.
- [ ] **Step 5:** Commit.

### Task 4: Encrypt server and relay secrets with automatic migration

**Files:**
- Create: `src/GameServerControl.Agent/Servers/ServerSecretProtection.cs`
- Modify: `src/GameServerControl.Agent/Servers/ServerRegistry.cs`
- Modify: `src/GameServerControl.Agent/Servers/ServerStore.cs`
- Modify: `src/GameServerControl.Agent/Networking/PublicRelayManager.cs`
- Modify: `tools/GameServerControl.SecurityCheck/GameServerControl.SecurityCheck.csproj`
- Modify: `tools/GameServerControl.SecurityCheck/Program.cs`

**Interfaces:**
- Consumes: `SecretProtector`.
- Produces: plaintext `ServerDef`/relay token only in memory; encrypted JSON on disk; automatic legacy migration.

- [ ] **Step 1:** Add behavior tests for plaintext legacy server migration, encrypted save/round-trip, encrypted `.bak`, relay token migration, and wrong-purpose failure.
- [ ] **Step 2:** Verify RED.
- [ ] **Step 3:** Implement shared server field codec and integrate it into registry/store; migrate both primary and backup state.
- [ ] **Step 4:** Integrate relay token protect/unprotect and one-time migration.
- [ ] **Step 5:** Verify GREEN.
- [ ] **Step 6:** Commit.

### Task 5: Preserve encryption state across installers/upgrades

**Files:**
- Modify: `deploy/windows/install.ps1`
- Modify: `deploy/linux/install-from-release.sh`
- Modify: `deploy/linux/install.sh`
- Modify: `scripts/validate-audit-sec08.mjs`

**Interfaces:**
- Consumes: `secret.key`, `secret-state.json`.
- Produces: upgrade/reinstall flows that preserve key/state and restore restrictive permissions.

- [ ] **Step 1:** Extend the regression contract to require preservation and permission hardening of both files in all relevant installers.
- [ ] **Step 2:** Verify RED.
- [ ] **Step 3:** Update Windows and Linux preserve lists; make source Linux reinstall preserve existing Agent state instead of deleting the encryption key.
- [ ] **Step 4:** Run PowerShell/bash syntax validation and SEC-08 validator.
- [ ] **Step 5:** Commit.

### Task 6: Documentation, full review, and merge

**Files:**
- Modify: `SECURITY.md`
- Modify as required by review findings only.

**Interfaces:**
- Produces: documented security boundary and verified merged fix.

- [ ] **Step 1:** Document encrypted state, key trust boundary, migration, and backup requirements; remove the obsolete plaintext-at-rest limitation.
- [ ] **Step 2:** Run full Windows/Linux CI on the final HEAD.
- [ ] **Step 3:** Review the complete PR diff for secret logging, migration downgrade paths, key-loss behavior, backup plaintext, and installer omissions.
- [ ] **Step 4:** Re-run CI if review changes the HEAD.
- [ ] **Step 5:** Confirm `main` has not moved from the PR base, mark ready, and squash merge.