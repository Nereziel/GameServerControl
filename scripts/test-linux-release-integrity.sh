#!/usr/bin/env bash
set -euo pipefail

# Exercise the actual checksum verification function extracted from each
# release installer, not a copy of its implementation.
root="$(cd "$(dirname "$0")/.." && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

for case in "deploy/linux/install-from-release.sh:GameServerControl-Agent-linux.tar.gz" \
            "deploy/relay/install-from-release.sh:GameServerControl-Relay-linux.tar.gz"; do
    installer="${case%%:*}"
    asset="${case#*:}"
    awk '/^verify_release_checksum\(\) \{$/ { active = 1 } active { print; if ($0 == "}") exit }' \
        "$root/$installer" > "$scratch/function.sh"
    if ! grep -q '^verify_release_checksum() {' "$scratch/function.sh"; then
        echo "Missing release-integrity verifier in $installer" >&2
        exit 1
    fi
    # shellcheck disable=SC1091
    source "$scratch/function.sh"
    printf 'Known release payload fixture\n' > "$scratch/archive.tar.gz"
    digest="$(sha256sum "$scratch/archive.tar.gz" | awk '{print $1}')"
    printf '%s  %s\n' "$digest" "$asset" > "$scratch/SHA256SUMS"
    verify_release_checksum "$scratch/SHA256SUMS" "$asset" "$scratch/archive.tar.gz" ||
        { echo "Valid release archive rejected: $installer" >&2; exit 1; }

    printf 'tampered' >> "$scratch/archive.tar.gz"
    if verify_release_checksum "$scratch/SHA256SUMS" "$asset" "$scratch/archive.tar.gz"; then
        echo "Tampered release accepted: $installer" >&2
        exit 1
    fi

    printf 'Known release payload fixture\n' > "$scratch/archive.tar.gz"
    printf '%s  %s\n' "$digest" "$asset" >> "$scratch/SHA256SUMS"
    if verify_release_checksum "$scratch/SHA256SUMS" "$asset" "$scratch/archive.tar.gz"; then
        echo "Duplicate checksum entry accepted: $installer" >&2
        exit 1
    fi

    printf '%s  another-file\n' "$digest" > "$scratch/SHA256SUMS"
    if verify_release_checksum "$scratch/SHA256SUMS" "$asset" "$scratch/archive.tar.gz"; then
        echo "Missing checksum entry accepted: $installer" >&2
        exit 1
    fi
    echo "$installer: checksums validated against valid, tampered, duplicate and missing inputs"
done
