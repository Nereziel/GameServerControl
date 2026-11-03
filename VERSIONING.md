# Versioning

GameServerControl uses stable [Semantic Versioning](https://semver.org/) in `MAJOR.MINOR.PATCH` form.

## Development builds

`Directory.Build.props` defines the default version for local development and ordinary CI builds. The helper `node scripts/version.mjs check` validates that it is well-formed.

You can adjust the development version with `node scripts/version.mjs bump patch|minor|major`, but **a version bump commit is no longer required to publish a release**.

## Releases

The **Git tag is the release version**. The Release workflow validates the tag and stamps every Windows/Linux Agent, Runner, Tray and Relay binary with the matching version, including assembly/file/informational metadata. It verifies Windows executable ProductVersion values before publishing assets.

1. Open **Actions → Release → Run workflow** on branch `main`.
2. Enter a new unused stable tag, for example `v1.1.5`.
3. The workflow validates the source, builds Windows/Linux packages, checks binary versions and publishes the GitHub Release.

Existing tags cannot be reused. Only stable tags (`vMAJOR.MINOR.PATCH`) are supported. A tag push also runs the release workflow; if creating a tag manually, point it at the intended commit.

Versioning guidance: increment **PATCH** for compatible fixes, **MINOR** for compatible features, **MAJOR** for breaking changes. The workflow does not infer the appropriate increment: the release operator selects the version.

The dedicated version-contract workflow was removed. Tag validation takes place inside the Release workflow, and normal CI checks the development version syntax in Build.
