# Release Validation Gate Design

## Goal

Prevent a GitHub release from being built when the current release source fails the repository's security, backup, Web UI, Cloudflare Worker, installer, relay, or game-profile validation contracts.

## Current state

`.github/workflows/build.yml` runs the full validation suite before its normal platform builds. `.github/workflows/release.yml` validates Linux installer syntax and Relay UFW reconciliation, then publishes release binaries, but it does not run the newer token-storage, access-control, ReadOnly UI, backup lifecycle/safety, mod/settings, profile, or Cloudflare Worker tests.

## Design

Add a single Ubuntu `validate` job to `.github/workflows/release.yml`. It checks out the exact release source resolved by `prepare`, installs .NET 8, then runs the same repository-owned validation commands that protect `main`, including the Cloudflare Worker unit/build/dry-run checks with non-secret CI placeholder values.

Both `build-windows` and `build-linux` depend on `validate` in addition to `prepare`. The release packaging jobs stay unchanged. This keeps one release gate in front of all platform artifacts without duplicating the full platform publish process.

## Security constraints

- Never use production Cloudflare or Agent credentials in validation.
- Use only invalid/example placeholder values for Wrangler dry-run secrets.
- Do not deploy Cloudflare resources from the release workflow.
- Do not publish a release unless the validation job succeeds.
- Continue building and committing only in `Nereziel/GameServerControl`.

## Acceptance criteria

1. Release validation runs the Web UI syntax, ReadOnly UI, global settings, mod management, backup safety, token storage, Agent security/access, backup lifecycle/UI, Relay UFW, profile, and Cloudflare Worker test/build/dry-run checks.
2. Windows and Linux release builds cannot start when validation fails.
3. Existing version stamping and release assets remain unchanged.
4. Normal `Build` workflow remains green after the workflow change.
