# GSC reliability roadmap — execution plan (2026-11-16)

This is a multi-subsystem roadmap; implement as independent pull requests. Scope is approved by the user's explicit instruction to carry out the recommended phases.

## PR A — Backup, release integrity, ZIP extraction
1. **Red:** Add `BackupRetentionPolicyTests` for complete/partial/safety retention classes; assert a partial result does not count as success; run integration CI and observe failure.
2. **Green:** Add `BackupRetentionPolicy.SelectBareMetalEvictions(IEnumerable<BackupInfo>, int)`. Integrate in `BackupService.PruneBareMetalAsync`. Return failure with retained artifact path when `SkippedFiles > 0`. Keep complete backup quota independent.
3. **Red:** Add ZIP extraction tests (compressed amplification, >4096 entries, Unix symlink) in `MinecraftProvisioningTests`; verify failure.
4. **Green:** In `MinecraftServerPackages.ExtractSafeZip` enforce entry count, total expanded sizes, ratio, symlink attributes, and bounded copy.
5. **Release integrity:** Update `deploy/linux/install-from-release.sh` and `deploy/relay/install-from-release.sh` to fetch SHA256SUMS and verify archive by sha256sum before any service mutation or extraction; verify with fixtures, shell -n and CI.
6. Verify Windows/Linux Build + Runtime Integration; merge A PR only when clean.

## PR B — Install continuity and protocol compatibility
1. Tests: interrupted install and cancellation, safe registration failure, Java version output.
2. Install operations: persist job transitions and resumable status; explicit cancellation token, no invisible orphan tasks; don't remove installed files when registration fails.
3. Client-server contract: advertise Agent API/features and require capability before enabling Cloudflare-only UI controls; keep ReadOnly projection safe.
4. Java: probe runtime version and warn/block unsupported version combinations.
5. Browser E2E and flaky CI test lifecycle; CI on both operating systems.

## PR C — Recovery + game enhancements
1. Design optional offsite backup integration with explicit setup, no automatic transmission.
2. Add an integrity-verified, opt-in off-machine backup flow; include restore drills.
3. Prototype supported Forge/NeoForge and modpack installation from trusted upstream artifacts, staged and reversible.
4. Improve CPU/RAM/disk monitoring and explanatory diagnostics.
5. Test and document each capability in separate PRs; production rollout remains manual.

## Review focus
- Last complete world snapshot must not be evicted by partial backups.
- Public download must never be extracted without verified release manifest.
- ZIP amplification, symlinks and paths must fail closed.
- Agent restart must not erase knowledge of an active installation or orphan a process silently.
- Different Cloudflare/Agent versions must not expose unsupported actions.
