# Releases

Releases are created from GitHub Actions with the manual **CD / Release** workflow.

The workflow:

- validates `VersionPrefix` in [`../Directory.Build.props`](../Directory.Build.props);
- enforces standard releases from `main` and patch releases from `patch/vX.Y.Z`;
- restores tools and packages on each release platform;
- builds, tests, publishes, and smoke-tests each self-contained runtime artifact;
- publishes portable archives for `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`;
- writes `mygamebuilder-local-release.json` and `SHA256SUMS.txt`;
- creates an annotated `vX.Y.Z` tag and GitHub Release with generated notes;
- opens a follow-up PR.

## Release Artifacts

| Platform | Runtime ID | Asset | Run command |
| --- | --- | --- | --- |
| Windows x64 | `win-x64` | `mygamebuilder-local-VERSION-win-x64.zip` | `.\mygamebuilder-local.exe` |
| Linux x64 | `linux-x64` | `mygamebuilder-local-VERSION-linux-x64.tar.gz` | `./mygamebuilder-local` |
| macOS Intel | `osx-x64` | `mygamebuilder-local-VERSION-osx-x64.tar.gz` | `./mygamebuilder-local` |
| macOS Apple Silicon | `osx-arm64` | `mygamebuilder-local-VERSION-osx-arm64.tar.gz` | `./mygamebuilder-local` |

Each archive is self-contained and does not require a preinstalled .NET runtime. After starting the app, open `http://127.0.0.1:3000`.

The release archives intentionally do not include legacy client, frontend, archive, or overlay data. The in-app setup/update page can install published archive releases when they are available. Users may also place optional local data next to the executable:

- `archive.sqlite` for imported read-only piece archive content.
- `frontend.sqlite` for the recovered frontend/client archive.

The app creates `overlay.sqlite` next to the executable when it starts. That writable local data is never included in release archives.

The app release manifest has this shape:

```json
{
  "version": "0.1.0",
  "tag": "v0.1.0",
  "assets": [
    {
      "rid": "win-x64",
      "name": "mygamebuilder-local-0.1.0-win-x64.zip",
      "sha256": "...",
      "sizeBytes": 123
    }
  ]
}
```

Archive releases are published from `OpenGameBuilder/mygamebuilder-archive` with tags `vX.Y.Z-client` for frontend/client files and `vX.Y.Z-s3` for S3 archive files. Releases may include `mgb-archive-manifest.json`, `SHA256SUMS.txt`, and the manifest-listed SQLite or split zstd SQLite assets. When no manifest asset is present, the app derives archive metadata from release assets named like `frontend.sqlite`, `archive.sqlite`, or `archive.sqlite.zst.part-000` and verifies their GitHub SHA-256 digests before validating the SQLite database.

macOS release binaries are unsigned. Depending on how the archive was downloaded, users may need to allow the binary in **System Settings > Privacy & Security** or remove the quarantine attribute from the extracted executable:

```sh
xattr -d com.apple.quarantine ./mygamebuilder-local
```

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
