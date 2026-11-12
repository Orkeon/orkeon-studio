# CLAUDE.md

Orkeon Studio is the family of front-ends over the Orkeon agent framework (.NET 10): a WPF desktop app (`Orkeon.Studio.Wpf`), two Terminal.Gui apps (`Orkeon.Studio.Config`, `Orkeon.Studio.Run`) and their UI-agnostic core (`Orkeon.Studio.Core`). Studio never runs a crew in-process: it drives the `orkeon` CLI, which must be installed (next to Studio, in `ORKEON_CLI_DIR`, or on `PATH`). The version lives in `src/Directory.Build.props` alone: never state it in prose. `git tag` is the authority on what is tagged.
Start from `docs/INDEX.md` (`docs/fr/` is its French mirror); the architecture is `docs/architecture/studio.md`. Breaking changes go in `CHANGELOG.md`. The framework, the CLI and the installers live in https://github.com/Orkeon/orkeon.

## Orkeon as packages

Orkeon is consumed through NuGet packages, never project references: `Orkeon`, `Orkeon.Constants.Cli`, `Orkeon.Cli.Abstractions`, `Orkeon.Cli.TerminalGui`, the `Orkeon.Compliance.Vfs` analyzer (wired in `src/Directory.Build.props`), and for the tests `Orkeon.Tools` and `Orkeon.Scripting` (which carries `Orkeon.Hosting.dll`). One `OrkeonVersion` property in `Directory.Packages.props` sets them all. They come from nuget.org (`nuget.config` has no other source); CI clones the Orkeon tag `v<OrkeonVersion>` for the drift tests, so moving `OrkeonVersion` moves both.
The drift tests of `Orkeon.Studio.Core.Tests` read files of an Orkeon checkout (`OrkeonSources.Root()`: `ORKEON_SOURCE_ROOT`, else `../orkeon`); without one they fail, never pass on nothing.

## Commands

The test runner is **Microsoft.Testing.Platform, not VSTest** (`global.json`). A `--filter` that matches zero tests in a module is an **error (exit 8)**: never exclude a project by name from a solution-wide run, and add `-- --ignore-exit-code 8` to a solution-wide filter most modules do not match. VSTest-only flags (`--collect`, `--logger`, `--blame`) are rejected.

```bash
dotnet build Orkeon.Studio.sln --no-restore            # CI builds -c Release -warnaserror: zero warnings
dotnet test Orkeon.Studio.sln --no-build --filter "Category!=Integration&Category!=Slow"   # what ci.yml runs
dotnet test tests/apps/Orkeon.Studio.Core.Tests/Orkeon.Studio.Core.Tests.csproj --no-build --filter "FullyQualifiedName~MyTest"
dotnet restore Orkeon.Studio.sln -p:NuGetAudit=false   # only when a dependency changed (see below)
python3 scripts/check-comment-accents.py               # CI runs it
```

## Network constraints (metered connection)

Bandwidth may be capped. Never re-download anything that is already cached.

- `dotnet build`: always pass `--no-restore`, unless a `.csproj`, `Directory.Packages.props` or `nuget.config` changed during the task.
- `dotnet test`: always pass `--no-build` once a build has succeeded in the task.
- `dotnet restore`: only when a dependency changed. Under Linux it reads and fills `<repository>/packages` (`RestorePackagesPath` in `Directory.Build.props`); that folder may be a symlink to another checkout's cache. Never override that path. An offline restore passes `-p:NuGetAudit=false` (CI keeps the audit).
- `git clone`: always `--depth 1 --filter=blob:none`; reuse an existing checkout.
- Never clear a NuGet cache or delete a cache directory. Clean up worktrees and temporary directories at the end of a task.

## Rules

### Branching: never start work on `main`

Before the first change, read `git branch --show-current`. On `main`, create `feature/<slug>`, `fix/<slug>` or `chore/<slug>` first. Nothing is committed directly to `main`.

### Virtual File System and console

Studio code never calls `System.IO.File.*`, `System.IO.Directory.*`, or `new FileStream/FileInfo/DirectoryInfo/FileSystemWatcher`, and never references `System.Console`: the `Orkeon.Compliance.Vfs` analyzer enforces both. Exceptions are explicit: bootstrap code marked `// EXCEPTION-BOOTSTRAP`, system probing marked `// OUT-OF-SCOPE`, and symbols carrying `[SuppressVfsCompliance("reason")]` / `[SuppressConsoleCompliance("reason")]` (one console sink per executable). Tests are exempt.

### Executable bit

Every `.sh` script, and every script with a shebang meant to be run directly, is tracked as mode `100755`: `git update-index --chmod=+x <path>`, then check `git ls-files -s`. `file-modes.yml` enforces it.

### Test doubles and assertions

Native xUnit assertions only: no mocking framework and no fluent assertion library. Doubles are hand-written classes named `Mock*`, `Fake*` or `Stub*` after the interface they implement, in the consuming test project's `Doubles/` (or `Fakes/`) folder, exposing plain fields. Tests hold when several passes run at once: no unit test bounds a duration.

### Other rules

- **Layout**: keep `src/apps/` and `tests/apps/`. Conformity tests read `../../../src/apps/Orkeon.Studio.Wpf`, and `Orkeon.Studio.Wpf.Tests` compiles `src/apps/Orkeon.Studio.Wpf/ViewModels/**/*.cs` by link: nothing under `ViewModels/` may reference a WPF type.
- **Name collisions**: `Orkeon.Domain.Task` collides with `System.Threading.Tasks.Task`, and `Orkeon.Application` with WPF's `Application`: write the full name where both are visible.
- **Comments** are English and unaccented (`scripts/check-comment-accents.py`); user-facing strings keep their accents and live in the `Resources/*.resx` catalogues.
- **Docs**: a changed docs page is mirrored in `docs/fr/` in the same commit; a link to an Orkeon page is an absolute `https://github.com/Orkeon/orkeon/blob/main/...` URL.
- **Commits** are in English, `type(scope): summary`.

## Architecture map

- `src/apps/Orkeon.Studio.Core`: settings model (`AppSettingsDocument`), presets, mounts, validation, run-target detection, launching and reading the `orkeon` CLI (`OrkeonBinaryLocator`, `OrkeonProcessRunner`, the JSONL event parser).
- `src/apps/Orkeon.Studio.Config`, `src/apps/Orkeon.Studio.Run`: Terminal.Gui renderings over Core.
- `src/apps/Orkeon.Studio.Wpf`: the WPF app; `net10.0-windows`, built by the Linux job too (`EnableWindowsTargeting`), and by the Windows jobs.
- `tests/apps/Orkeon.Studio.*.Tests`: one suite per project; `tests/shared/Orkeon.Studio.Tests.Shared`: shared fixtures (namespaces `Orkeon.Tests.Shared.*`) and `OrkeonSources`.
- `scripts/`: `check-comment-accents.py`, `check-console-urls.py` (manual: lists or checks the API-key console links of the provider catalogue), `install-from-source.sh`/`.ps1` (installs Studio from a clone next to the CLI: packs Orkeon into the git-ignored `orkeon-feed/` at a dated version and restores through a `nuget.config` it writes, builds under `StudioBuildRoot`), `resolve-version.sh`/`.ps1` (copied from Orkeon: keep them in step).
- Release: `.github/workflows/release.yml` on a `v*` tag; `scripts/package-studio.sh` (win-x64 zip, linux-x64 tar.gz), `scripts/build-msi.ps1` + `installers/msi/Package.wxs` (WiX v5, `.config/dotnet-tools.json`), `scripts/smoke-studio-{linux.sh,windows.ps1}`. See `docs/reference/release.md`.
