# Orkeon.Studio.Wpf

Part of [Orkeon](https://github.com/Orkeon/orkeon) — build and orchestrate AI agent teams in .NET, described in declarative YAML, programmatic TypeScript (`.ork.ts`) or pure C#.

**Orkeon.Studio.Wpf** is the Orkeon Studio desktop application: the team-creation wizard
(Describe ▸ Compose ▸ Try ▸ Adopt over `orkeon forge --events jsonl`), the teams screen,
the run screen and the unified settings screen. It targets `net10.0-windows` and its
assembly is named `Orkeon.Studio`; the launcher shipped in the archives is
`orkeon-studio` (`win-x64` only — WPF runs on Windows alone). Light/dark themes and a hot
five-language switch (en, fr, es, de, zh-Hans) come from `Resources/Strings.resx` and its
four satellites.

## Distribution

`IsPackable=false`: on no NuGet feed. It ships in `orkeon-studio-<version>-win-x64.zip`
and in the per-user `orkeon-studio-<version>-win-x64.msi` (with an "Orkeon Studio"
Start-menu shortcut) — see [Release](../../../docs/reference/release.md).

## Documentation

- [Studio architecture](../../../docs/architecture/studio.md)
- [Three ways to run Orkeon](https://github.com/Orkeon/orkeon/blob/main/docs/getting-started/three-ways-to-run-orkeon.md)

MIT © Orkeon Contributors
