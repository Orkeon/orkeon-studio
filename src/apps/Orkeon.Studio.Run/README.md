# Orkeon.Studio.Run

Part of [Orkeon](https://github.com/Orkeon/orkeon) — build and orchestrate AI agent teams in .NET, described in declarative YAML, programmatic TypeScript (`.ork.ts`) or pure C#.

**Orkeon.Studio.Run** is the Studio run terminal app: pick a crew, launch it through the
shared process runner, and watch the run's events stream back. It builds on
`Orkeon.Studio.Core` and renders with Terminal.Gui.

## Distribution

`IsPackable=false`: on no NuGet feed. It ships as the `orkeon-studio-run` launcher in
`orkeon-studio-<version>-linux-x64.tar.gz`, the Linux archive of Orkeon Studio's own
release — see [Release](../../../docs/reference/release.md).

## Documentation

- [Studio architecture](../../../docs/architecture/studio.md)
- [CLI reference](https://github.com/Orkeon/orkeon/blob/main/docs/reference/cli.md)

MIT © Orkeon Contributors
