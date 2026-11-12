> 🇫🇷 [Version française](CONTRIBUTING.fr.md)

# Contributing to Orkeon Studio

## Code of Conduct

This project and everyone participating in it is governed by our [Code of Conduct](CODE_OF_CONDUCT.md). By participating, you are expected to uphold this code.

## Reporting bugs and suggesting enhancements

Both are GitHub issues. Give a clear title, the exact steps, what you observed and what you expected, and the output of `orkeon doctor` when the problem involves a run. A problem in the engine, the CLI or the configuration it reads belongs to the [Orkeon repository](https://github.com/Orkeon/orkeon/issues).

## Pull requests

1. Create your branch from `main`: `feature/<slug>`, `fix/<slug>` or `chore/<slug>`. Nothing is committed to `main` directly.
2. Add tests for the code you add, and keep the suite green.
3. Update the documentation, in English and in its French mirror under `docs/fr/`.
4. On your first pull request, accept the [Contributor License Agreement](CLA.md).

## Development setup

Prerequisites: the .NET SDK pinned in `global.json`, Python 3, and the `orkeon` CLI to run the applications (see the [README](README.md)).

```bash
dotnet restore Orkeon.Studio.sln     # once, and again only when a package reference changes
dotnet build Orkeon.Studio.sln --no-restore                 # CI builds -c Release -warnaserror: zero warnings
dotnet test Orkeon.Studio.sln --no-build --filter "Category!=Integration&Category!=Slow"   # what CI runs
```

The test runner is **Microsoft.Testing.Platform, not VSTest** (`global.json`). A `--filter` that matches zero tests in a module is an error (exit 8): add `-- --ignore-exit-code 8` to a solution-wide filter most modules do not match. VSTest-only flags (`--collect`, `--logger`, `--blame`) are rejected.

Studio consumes Orkeon as NuGet packages, at the version `OrkeonVersion` sets in `Directory.Packages.props`. The drift tests of `Orkeon.Studio.Core.Tests` read files of an Orkeon checkout: set `ORKEON_SOURCE_ROOT`, or clone Orkeon as `../orkeon`.

The packages come from nuget.org. To build against a newer Orkeon, move `OrkeonVersion` to a published version: CI clones the Orkeon tag `v<OrkeonVersion>` for the drift tests, so the two move together, and a local `../orkeon` should be checked out at that tag too. Never download packages again that the `packages/` cache already holds.

## Project structure

| Path | Content |
|---|---|
| `src/apps/Orkeon.Studio.Core` | UI-agnostic core: settings model, presets, mounts, validation, launching the CLI. |
| `src/apps/Orkeon.Studio.Config` | Terminal.Gui settings editor. |
| `src/apps/Orkeon.Studio.Run` | Terminal.Gui crew launcher. |
| `src/apps/Orkeon.Studio.Wpf` | WPF desktop app (`net10.0-windows`). |
| `tests/apps/Orkeon.Studio.*.Tests` | One test project per application. `Orkeon.Studio.Wpf.Tests` compiles the WPF ViewModels by link and runs on Linux. |
| `tests/shared/Orkeon.Studio.Tests.Shared` | Shared fixtures (namespaces `Orkeon.Tests.Shared.*`, copied from Orkeon). |
| `docs/` | Architecture, with its French mirror in `docs/fr/`. |
| `scripts/` | Repository checks. |

## Rules the build enforces

* **No direct `System.IO` or `System.Console`.** Files go through `IFileSystemService`; the `Orkeon.Compliance.Vfs` analyzer fails the build otherwise. Bootstrap code is marked `// EXCEPTION-BOOTSTRAP`, system probing `// OUT-OF-SCOPE`, and a symbol that must touch the disk or the console carries `[SuppressVfsCompliance("reason")]` or `[SuppressConsoleCompliance("reason")]`. See [VFS compliance](https://github.com/Orkeon/orkeon/blob/main/docs/architecture/vfs-compliance.md) and [console compliance](https://github.com/Orkeon/orkeon/blob/main/docs/architecture/console-compliance.md).
* **`Orkeon.Domain.Task` shadows `System.Threading.Tasks.Task`**, and `Orkeon.Application` shadows WPF's `Application`: in a file that sees both, write the full name.
* **Zero warnings**: CI builds with `-warnaserror` under the full analyzer set (`AnalysisMode=All`).

## Comments are English, and never accented

`scripts/check-comment-accents.py` fails CI on a comment carrying an accented letter or French. Studio's interface is translated, so the trap is a comment quoting a UI label: translate the label, do not strip its accents. User-facing strings keep their accents.

## Testing

* xUnit and its native assertions only: no mocking framework (Moq, NSubstitute, FakeItEasy) and no fluent assertion library (FluentAssertions, Shouldly, Verify).
* Test doubles are hand-written classes named after the interface they replace, prefixed `Mock`, `Fake` or `Stub`, in a `Doubles/` (or `Fakes/`) folder of the consuming test project, exposing plain fields to configure answers and inspect calls.
* Tests hold when several passes run at once: a delay the test must not reach is a hang guard (`Polling.DefaultTimeout`), and no unit test bounds a duration.
* A test needing Docker, a network service or minutes of runtime carries `[Trait("Category", "Integration")]` or `[Trait("Category", "Slow")]`.

## Scripts and commits

* A `.sh` script, or any script with a shebang meant to be run directly, is committed executable (mode `100755`): `git update-index --chmod=+x <path>`. The `file-modes.yml` gate reads the mode from the git index.
* Commit messages are in English and follow the `type(scope): summary` shape (`feat`, `fix`, `docs`, `test`, `chore`).

## License

By contributing, you agree that your contributions are licensed under the MIT License ([LICENSE.md](LICENSE.md)) and accept the [CLA](CLA.md).
