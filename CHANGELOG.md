# Changelog

All notable changes to Orkeon Studio are recorded here. Breaking changes come first in each release, with a migration table when one applies.

## Unreleased

### Own release pipeline

Orkeon Studio is released from this repository, on its own version, and no longer through Orkeon's installers. The `orkeon` CLI becomes a prerequisite installed separately (`dotnet tool install -g Orkeon.Scripting.Cli`, or Orkeon's installers).

- A `v*` tag runs `.github/workflows/release.yml`: it refuses a tag that does not match `src/Directory.Build.props`, builds and tests, packages, smokes on Windows and Linux, then attaches the assets and one `SHA256SUMS` to a draft GitHub Release (marked prerelease when the version has a suffix).
- Assets: `orkeon-studio-<version>-win-x64.zip` and `orkeon-studio-<version>-win-x64.msi` (the WPF app; per-user MSI in `%LOCALAPPDATA%\Programs\Orkeon Studio` with a Start-menu shortcut, its own UpgradeCode), and `orkeon-studio-<version>-linux-x64.tar.gz` (`orkeon-studio-config` and `orkeon-studio-run`). Every app is self-contained, untrimmed, not single-file, as Orkeon published them.
- `scripts/package-studio.sh` builds the archives, `scripts/build-msi.ps1` the MSI (WiX v5, pinned in `.config/dotnet-tools.json`), `scripts/smoke-studio-linux.sh` and `scripts/smoke-studio-windows.ps1` smoke them.
- Studio no longer finds the CLI next to itself in an Orkeon install: on Windows, a user of Orkeon's installers sets `ORKEON_CLI_DIR` to its `libexec\orkeon` folder (the .NET tool needs nothing).

### Extracted from Orkeon

Orkeon Studio now lives in its own repository. The four projects (`Orkeon.Studio.Core`, `Orkeon.Studio.Config`, `Orkeon.Studio.Run`, `Orkeon.Studio.Wpf`) and their four test projects were copied from the Orkeon repository as a snapshot, without history.

- Orkeon is consumed through NuGet packages instead of project references: `Orkeon`, `Orkeon.Constants.Cli`, `Orkeon.Cli.Abstractions`, `Orkeon.Cli.TerminalGui`, the `Orkeon.Compliance.Vfs` analyzer, and, for the tests only, `Orkeon.Tools` and `Orkeon.Scripting`. One `OrkeonVersion` property in `Directory.Packages.props` sets their version.
- `Orkeon.Studio.Core` now references the `Orkeon` umbrella package, which carries `Orkeon.Infrastructure` and its dependencies, where it used to reference only `Orkeon.Domain`, `Orkeon.Rag.Abstractions` and the `Orkeon.Constants.*` satellites: a self-contained publish is heavier until Orkeon ships slimmer packages.
- The drift tests that read files Orkeon ships but does not package now read them from an Orkeon checkout named by `ORKEON_SOURCE_ROOT`, or `../orkeon`.
- The shared test fixtures Studio uses were copied from `Orkeon.Tests.Shared` into `Orkeon.Studio.Tests.Shared`, namespaces unchanged.
- Studio has its own version (`VersionPrefix`/`VersionSuffix` in `src/Directory.Build.props`), independent of Orkeon's.
- `scripts/install-from-source.sh` (Linux, macOS) and `scripts/install-from-source.ps1` (Windows) install Studio from a clone into an Orkeon CLI's installation, where Studio finds the CLI next to itself, optionally installing that CLI first from an Orkeon checkout.
- The local Orkeon feed moved from `../orkeon-feed` to `orkeon-feed/` at the repository root (git-ignored): move or re-pack your packages there, or the restore fails.
