> 🇫🇷 [Version française](README.fr.md)

# Orkeon Studio

[![Build](https://github.com/Orkeon/orkeon-studio/actions/workflows/ci.yml/badge.svg)](https://github.com/Orkeon/orkeon-studio/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Orkeon/orkeon-studio)](https://github.com/Orkeon/orkeon-studio/releases)
[![Documentation](https://img.shields.io/badge/documentation-docs-blue.svg)](docs/INDEX.md)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE.md)

<p align="center">
  <img src="docs/assets/orkeon-studio.png" alt="Orkeon Studio: the chameleon blacksmith at its anvil" width="280">
</p>

Orkeon Studio is the family of graphical and terminal front-ends over the [Orkeon](https://github.com/Orkeon/orkeon) agent framework: edit and validate the `appsettings.json` that `orkeon init` writes, create a team, then pick a crew and launch it by running the `orkeon` CLI.

| Application | Launcher | Platform | Role |
|---|---|---|---|
| `Orkeon.Studio.Wpf` | `orkeon-studio` | Windows | Desktop app: settings, teams, runs, in one window. |
| `Orkeon.Studio.Config` | `orkeon-studio-config` | Linux, macOS, Windows (terminal) | Terminal.Gui settings editor. |
| `Orkeon.Studio.Run` | `orkeon-studio-run` | Linux, macOS, Windows (terminal) | Terminal.Gui crew launcher. |

All three share `Orkeon.Studio.Core`, a UI-agnostic library. See [docs/architecture/studio.md](docs/architecture/studio.md).

## Studio drives the `orkeon` CLI

Studio never runs a crew in-process: it starts the `orkeon` CLI and reads its event stream. The CLI must be installed, and Studio looks for it in this order: the `--cli-dir` argument (WPF app only), next to the Studio executable, the `ORKEON_CLI_DIR` environment variable, `PATH`, on Windows the folder of the Orkeon installer (`%LOCALAPPDATA%\Programs\Orkeon\libexec\orkeon`), then, in a clone, the CLI built in the Orkeon checkout. Install it with the [Orkeon installers](https://github.com/Orkeon/orkeon/blob/main/docs/reference/publication-matrix.md) or as a .NET tool, and check it with `orkeon doctor`.

## Install

Download the asset for your platform from the [Releases](https://github.com/Orkeon/orkeon-studio/releases) page and check it against the `SHA256SUMS` file attached next to it (`sha256sum -c SHA256SUMS --ignore-missing`).

| Asset | Platform | What it holds |
|---|---|---|
| `orkeon-studio-<version>-win-x64.msi` | Windows x64 | Per-user installer: the desktop app in `%LOCALAPPDATA%\Programs\Orkeon Studio`, an "Orkeon Studio" Start-menu shortcut. |
| `orkeon-studio-<version>-win-x64.zip` | Windows x64 | The same desktop app: extract it anywhere, start `bin\orkeon-studio.cmd`. |
| `orkeon-studio-<version>-linux-x64.tar.gz` | Linux x64 | The two terminal apps, `bin/orkeon-studio-config` and `bin/orkeon-studio-run`: extract it and add `bin/` to `PATH`. |

Every application is self-contained: no .NET runtime is needed. The packages are not code-signed yet, so Windows SmartScreen warns on the first start.

**Prerequisite: the `orkeon` CLI, installed.** Studio does not ship it. Install it as a .NET tool, `dotnet tool install -g Orkeon.Scripting.Cli`, or with the [Orkeon installers](https://github.com/Orkeon/orkeon/releases), then check it with `orkeon doctor`. On Windows, Studio runs `orkeon.exe` itself: the .NET tool puts one on `PATH`; after an Orkeon installer, whose `bin\` on `PATH` holds `.cmd` launchers, Studio follows `bin\orkeon.cmd` to the `orkeon.exe` in `libexec\orkeon`, and looks in `%LOCALAPPDATA%\Programs\Orkeon\libexec\orkeon` even when `PATH` does not list it. Nothing to set: `ORKEON_CLI_DIR` is only for a CLI kept elsewhere. The launchers and schedules Studio writes for a team run `orkeon` from `PATH`, so they need the installer's `bin\` there, as the installer leaves it.

How a release is built: [docs/reference/release.md](docs/reference/release.md).

## Building from source

Prerequisites: the .NET SDK pinned in `global.json`, and Python 3 for the scripts under `scripts/`.

```bash
dotnet restore Orkeon.Studio.sln
dotnet build Orkeon.Studio.sln --no-restore
dotnet test Orkeon.Studio.sln --no-build --filter "Category!=Integration&Category!=Slow"
```

Studio consumes Orkeon as NuGet packages (`Orkeon`, `Orkeon.Constants.Cli`, `Orkeon.Cli.Abstractions`, `Orkeon.Cli.TerminalGui`; the tests add `Orkeon.Tools` and `Orkeon.Scripting`). Their version is the `OrkeonVersion` property in `Directory.Packages.props`. They come from nuget.org, like every other package (`nuget.config`).

The drift tests of `Orkeon.Studio.Core.Tests` pin Studio against files Orkeon ships but does not package (example crews, the installer's sample settings, the tool inventory, the CLI sources). They read them from an Orkeon checkout: set `ORKEON_SOURCE_ROOT` to one, or clone Orkeon next to this repository as `../orkeon`.

Run an application from source:

```bash
dotnet run --project src/apps/Orkeon.Studio.Config
dotnet run --project src/apps/Orkeon.Studio.Run
dotnet run --project src/apps/Orkeon.Studio.Wpf      # Windows only
```

## Installing from source

`scripts/install-from-source.sh` (Linux, macOS) and `scripts/install-from-source.ps1` (Windows, PowerShell 7) install Studio from this clone, next to an Orkeon CLI. With `--with-orkeon` (`-WithOrkeon`), they first install the CLI from the Orkeon checkout (`--orkeon-src`, else `ORKEON_SOURCE_ROOT`, else `../orkeon`) with that checkout's own `install-from-source` script.

```bash
./scripts/install-from-source.sh --with-orkeon            # CLI, then Studio, under ~/.local
./scripts/install-from-source.sh                          # Studio only, next to an installed CLI
./scripts/install-from-source.sh --dry-run                # prerequisites, versions, commands
./scripts/install-from-source.sh --uninstall              # removes this script's Studio, keeps the CLI
```

```powershell
pwsh -File .\scripts\install-from-source.ps1 -WithOrkeon  # Windows: the CLI, then Studio
```

The script packs the Orkeon packages into `orkeon-feed/` at a version dated by the Orkeon checkout's last commit (see `scripts/resolve-version.sh`) and builds Studio against them: a new Orkeon commit gives a new version, never a stale copy from the NuGet cache. It refuses to pack an Orkeon checkout with uncommitted changes, or to reuse packages of the same version packed from another commit. That build reads a `nuget.config` the script writes, which resolves `Orkeon.*` from `orkeon-feed/`; the repository's own reads nuget.org alone. Studio goes into the CLI's installation (`lib/orkeon/libexec`, or `libexec\` of `%LOCALAPPDATA%\Programs\Orkeon`), where it finds the CLI first. The CLI's installer replaces that directory whole: after reinstalling the CLI, run the script again.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md), the [code of conduct](CODE_OF_CONDUCT.md) and the [security policy](SECURITY.md). Contributions are accepted under the [CLA](CLA.md).

## License

MIT: see [LICENSE.md](LICENSE.md) and [NOTICE.md](NOTICE.md).
