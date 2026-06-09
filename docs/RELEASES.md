# Releases

Releases are created from GitHub Actions with the manual **CD / Release** workflow.

The workflow:

- validates `VersionPrefix` in [`../Directory.Build.props`](../Directory.Build.props);
- enforces standard releases from `main` and patch releases from `patch/vX.Y.Z`;
- restores tools and packages;
- builds and tests the solution;
- publishes self-contained archives for `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`;
- writes a `SHA256SUMS.txt` file;
- creates an annotated `vX.Y.Z` tag and GitHub Release with generated notes;
- opens a follow-up PR.

## Standard Release

1. Ensure `Directory.Build.props` has the release version, such as `0.1.0`.
2. Run **CD / Release** from GitHub Actions with `ref` set to `main`.
3. After the release succeeds, merge the generated version-bump PR.

Standard releases must use a patch version of `0`, for example `0.1.0` or `0.2.0`.

## Patch Release

1. Run **Prepare Patch** from GitHub Actions.
2. Merge the generated prepare PR into the new `patch/vX.Y.Z` branch.
3. Add patch fixes to that branch with normal pull requests.
4. Run **CD / Release** with `ref` set to the patch branch, such as `patch/v0.1.1`.
5. After the release succeeds, merge the generated merge-back PR into `main`.

Patch releases must patch the latest release line and increment the patch number by exactly one.

