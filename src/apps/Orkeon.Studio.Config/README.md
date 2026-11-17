# Orkeon.Studio.Config

Part of [Orkeon](https://github.com/Orkeon/orkeon) — build and orchestrate AI agent teams in .NET, described in declarative YAML, programmatic TypeScript (`.ork.ts`) or pure C#.

**Orkeon.Studio.Config** is the Studio configuration terminal app: the `orkeon init` flows
— model profiles, authorized folders, and the raw settings file — for platforms the WPF
desktop app does not reach. It builds on `Orkeon.Studio.Core` and renders with Terminal.Gui.

## Distribution

`IsPackable=false`: on no NuGet feed. It ships as the `orkeon-studio-config` launcher in
`orkeon-studio-<version>-linux-x64.tar.gz`, the Linux archive of Orkeon Studio's own
release — see [Release](../../../docs/reference/release.md).

## Documentation

- [Studio architecture](../../../docs/architecture/studio.md)
- [Configuration reference](https://github.com/Orkeon/orkeon/blob/main/docs/reference/configuration.md)

MIT © Orkeon Contributors
