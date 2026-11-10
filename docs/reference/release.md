> 🇫🇷 [Version française](../fr/reference/release.md)

# Release

Orkeon Studio is released from this repository, on its own version: `VersionPrefix` and `VersionSuffix` in `src/Directory.Build.props`, the single source of truth. The `orkeon` CLI is released by [Orkeon](https://github.com/Orkeon/orkeon) and installed separately: Studio never bundles it.

## Artifacts

| Asset | Built by | Contents |
|---|---|---|
| `orkeon-studio-<version>-win-x64.zip` | `scripts/package-studio.sh` | `bin\orkeon-studio.cmd`, `libexec\orkeon-studio\` (the WPF app, `Orkeon.Studio.exe`). |
| `orkeon-studio-<version>-win-x64.msi` | `scripts/build-msi.ps1` | The zip's tree, per user, in `%LOCALAPPDATA%\Programs\Orkeon Studio`, plus an "Orkeon Studio" Start-menu shortcut. No `PATH` entry. |
| `orkeon-studio-<version>-linux-x64.tar.gz` | `scripts/package-studio.sh` | `bin/orkeon-studio-config`, `bin/orkeon-studio-run`, `libexec/<app>/` (identical files hard-linked, stored once). |
| `SHA256SUMS` | the release workflow | One line per asset. |

Every archive also carries `README.md`, `LICENSE.md`, `NOTICE.md`, `VERSION`, and under `licenses/<pack>/` the license and notices of the .NET runtime packs it bundles. Each app is published self-contained (`--self-contained true`), untrimmed (`PublishTrimmed=false`) and not single-file, the flags Orkeon used for Studio. The MSI's `UpgradeCode` (`installers/msi/Package.wxs`) is generated once and never changed: it is how an upgrade recognizes an earlier Orkeon Studio. The MSI `ProductVersion` drops the suffix; the full version stays in the file name and in the Add/Remove Programs comment.

## The workflow

`.github/workflows/release.yml` runs on a `v*` tag (and on `workflow_dispatch`, without the publication):

1. `build-test`: refuses a tag that is not `v` + the props version, then builds with `-warnaserror` and runs the tests, as `ci.yml` does.
2. `package` (Ubuntu): `scripts/package-studio.sh` cross-publishes both RIDs; the WPF project sets `EnableWindowsTargeting`.
3. `smoke-linux` (Ubuntu): `scripts/smoke-studio-linux.sh` extracts the tarball and checks that both terminal apps answer `--version` with no terminal attached.
4. `msi` (Windows): `scripts/smoke-studio-windows.ps1` runs `orkeon-studio.cmd --smoke-exit` on the extracted zip (the window opens, renders and closes with exit code 0), `scripts/build-msi.ps1` builds the MSI with WiX v5, which is then installed, smoked again with its shortcut, and uninstalled.
5. `release` (tag only): every asset is checked against `SHA256SUMS`, then attached to a **draft** GitHub Release, marked prerelease when the version has a suffix. The draft is published by hand.

## Cutting a release

1. Set `VersionPrefix`/`VersionSuffix` in `src/Directory.Build.props` and move the `Unreleased` section of `CHANGELOG.md` under that version, in a pull request.
2. Once merged, tag the merge commit `v<version>` and push the tag.
3. When the workflow is green, review the draft Release and publish it.

## Packaging locally

```bash
./scripts/package-studio.sh --rids linux-x64      # artifacts/release/orkeon-studio-<version>-linux-x64.tar.gz
./scripts/smoke-studio-linux.sh artifacts/release/orkeon-studio-*-linux-x64.tar.gz
```

The first publish for a RID restores its runtime packs. `--no-restore` skips that restore when each project was already restored with the same `-r <rid>` and `-p:SelfContained=true`. The MSI builds on Windows only: `pwsh scripts/build-msi.ps1 -StageDir <the zip's extracted folder>`.
