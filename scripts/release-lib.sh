#!/usr/bin/env bash
# Shared helpers for release-related scripts.
# Sourced from other scripts in this directory. Not meant to be executed directly.

set -euo pipefail

# Plain SemVer X.Y.Z without prerelease/build metadata.
SEMVER_REGEX='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'

repo_root() {
    git rev-parse --show-toplevel
}

props_path() {
    echo "$(repo_root)/Directory.Build.props"
}

assert_semver() {
    local version="$1"
    if [[ ! "$version" =~ $SEMVER_REGEX ]]; then
        echo "Version must be plain SemVer X.Y.Z. Received: '$version'." >&2
        exit 1
    fi
}

semver_parts() {
    local version="$1"
    assert_semver "$version"
    echo "${version//./ }"
}

semver_compare() {
    local left="$1"
    local right="$2"
    local l_parts r_parts
    read -r -a l_parts <<< "$(semver_parts "$left")"
    read -r -a r_parts <<< "$(semver_parts "$right")"
    for i in 0 1 2; do
        if (( l_parts[i] < r_parts[i] )); then echo -1; return; fi
        if (( l_parts[i] > r_parts[i] )); then echo 1; return; fi
    done
    echo 0
}

read_version() {
    local props
    props="$(props_path)"
    local count
    count="$(grep -c '<VersionPrefix>' "$props" || true)"
    if [ "$count" != "1" ]; then
        echo "Expected exactly one <VersionPrefix> in $props, found $count." >&2
        exit 1
    fi
    local version
    version="$(sed -n 's|.*<VersionPrefix>\([^<]*\)</VersionPrefix>.*|\1|p' "$props" | head -n 1 | tr -d '[:space:]')"
    assert_semver "$version"
    echo "$version"
}

write_version() {
    local new_version="$1"
    assert_semver "$new_version"
    local props
    props="$(props_path)"
    sed -i.bak "s|<VersionPrefix>[^<]*</VersionPrefix>|<VersionPrefix>${new_version}</VersionPrefix>|" "$props"
    rm -f "${props}.bak"
}

latest_release_tag() {
    local line_filter="${1:-}"
    local jq_filter='.[] | select(.isDraft == false and .isPrerelease == false) | .tagName | select(test("^v[0-9]+\\.[0-9]+\\.[0-9]+$"))'
    local tags
    if ! tags="$(gh release list --limit 200 --json 'tagName,isDraft,isPrerelease' --jq "$jq_filter")"; then
        echo "Failed to list GitHub Releases. Is GH_TOKEN set with the right permissions?" >&2
        exit 1
    fi
    if [ -n "$line_filter" ]; then
        if [[ ! "$line_filter" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
            echo "Line filter must look like X.Y. Received: '$line_filter'." >&2
            exit 1
        fi
        tags="$(echo "$tags" | grep -E "^v${line_filter//./\\.}\\.[0-9]+$" || true)"
    fi
    echo "$tags" | sed 's/^v//' | sort -t. -k1,1n -k2,2n -k3,3n | tail -n 1 | sed 's/^/v/' | sed 's/^v$//'
}

gh_output() {
    local name="$1"
    local value="$2"
    if [ -n "${GITHUB_OUTPUT:-}" ]; then
        echo "${name}=${value}" >> "$GITHUB_OUTPUT"
    else
        echo "${name}=${value}"
    fi
}

configure_release_bot_git() {
    git config user.name "github-actions[bot]"
    git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
}
