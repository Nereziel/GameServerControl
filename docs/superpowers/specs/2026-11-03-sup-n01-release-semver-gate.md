# SUP-N01 Release SemVer Gate

## Problem

`Directory.Build.props` is the repository source of truth for the stable product version, and `scripts/version.mjs check <tag>` can validate an exact `vMAJOR.MINOR.PATCH` tag against it. The standalone Version contract workflow performs that check for tag-triggered runs, but `.github/workflows/release.yml` does not enforce it in its own release job graph.

A manually dispatched Release workflow can therefore accept a syntactically valid tag that differs from the repository version and continue toward publishing artifacts stamped with the supplied tag version.

## Required behavior

- The Release workflow's `validate` job must run `node scripts/version.mjs check` against the exact tag resolved by `prepare`.
- Windows and Linux build jobs must remain gated by the existing `validate` job.
- `scripts/validate-release-workflow.mjs` must enforce the presence of the exact SemVer gate so it cannot be removed silently.
- The standalone Version contract remains useful defense in depth but is not the publication gate.
- Stable SemVer only; prerelease behavior is unchanged.
