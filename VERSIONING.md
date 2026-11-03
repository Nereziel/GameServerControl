# Versioning

GameServerControl uses stable [Semantic Versioning](https://semver.org/) in `MAJOR.MINOR.PATCH` form.

`Directory.Build.props` is the single source of truth for the product version. Do not duplicate the version in project files or add separate `AssemblyVersion`, `FileVersion`, or `InformationalVersion` values there. Release builds may override those assembly metadata fields from the validated release version so packaged binaries keep an exact `ProductVersion`.

## Choosing the next version

- **PATCH** (`1.1.2` → `1.1.3`): backwards-compatible fixes, hardening, packaging fixes, or small internal changes.
- **MINOR** (`1.1.2` → `1.2.0`): backwards-compatible features or meaningful new capabilities.
- **MAJOR** (`1.1.2` → `2.0.0`): intentional breaking changes to supported configuration, APIs, protocols, deployment contracts, or other public behavior.

Pre-release identifiers such as `-alpha`, `-beta`, or `-rc.1` are intentionally not accepted yet. The current update checker and release workflow are designed around stable releases.

## Bumping

Use the repository helper instead of editing several version fields manually:

```bash
node scripts/version.mjs bump patch
node scripts/version.mjs bump minor
node scripts/version.mjs bump major
```

The command updates only `Directory.Build.props`. Review and commit that version change together with the release-ready code.

Useful read-only commands:

```bash
node scripts/version.mjs current
node scripts/version.mjs tag
node scripts/version.mjs check
```

`tag` prints the matching Git tag, for example `v1.1.3`.

## Releasing

A stable release tag must exactly match the committed repository version:

```bash
node scripts/version.mjs check "$(node scripts/version.mjs tag)"
```

The `Version contract` GitHub Actions workflow runs on pull requests, pushes, and `v*` tags. On a tag build it rejects a tag whose `vMAJOR.MINOR.PATCH` does not exactly match `Directory.Build.props`.

The normal sequence is therefore:

1. Choose `patch`, `minor`, or `major` according to the compatibility impact.
2. Run `node scripts/version.mjs bump <part>`.
3. Commit and merge the version bump with the release-ready changes.
4. Create the tag printed by `node scripts/version.mjs tag` from that commit.
5. Let the existing release workflow build and stamp the artifacts with that release version.
