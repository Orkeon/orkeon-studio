#!/usr/bin/env bash
# Packs, from an Orkeon checkout, every Orkeon package this repository restores into
# orkeon-feed/, at the OrkeonVersion of Directory.Packages.props.
#
#   scripts/pack-orkeon-feed.sh <orkeon-checkout> [feed-dir]
#
# TEMPORARY, like the orkeon-local source of nuget.config: CI and the release pipeline run it
# because the packages are not published on nuget.org yet. Once they are, drop this script
# and its workflow steps, and set OrkeonVersion to the published version.
#
# The list covers the apps (the five packages scripts/install-from-source.* packs, keep the
# two in step) and the tests (Orkeon.Tools, Orkeon.Scripting and the
# Orkeon.Tools.Embeddings.Local package Orkeon.Scripting depends on).
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "usage: $0 <orkeon-checkout> [feed-dir]" >&2
  exit 2
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ORKEON_SRC="$(cd "$1" && pwd)"
FEED="${2:-$REPO_ROOT/orkeon-feed}"

VERSION="$(sed -n 's|.*<OrkeonVersion>\(.*\)</OrkeonVersion>.*|\1|p' "$REPO_ROOT/Directory.Packages.props")"
[[ -n "$VERSION" ]] || { echo "pack-orkeon-feed: OrkeonVersion not found in Directory.Packages.props" >&2; exit 1; }

PACKAGES=(
  "Orkeon|src/packaging/Orkeon/Orkeon.csproj"
  "Orkeon.Constants.Cli|src/constants/Orkeon.Constants.Cli/Orkeon.Constants.Cli.csproj"
  "Orkeon.Cli.Abstractions|src/cli/Orkeon.Cli.Abstractions/Orkeon.Cli.Abstractions.csproj"
  "Orkeon.Cli.TerminalGui|src/packaging/Orkeon.Cli.TerminalGui/Orkeon.Cli.TerminalGui.Package.csproj"
  "Orkeon.Compliance.Vfs|src/analyzers/Orkeon.Compliance.Vfs/Orkeon.Compliance.Vfs.csproj"
  "Orkeon.Tools|src/packaging/Orkeon.Tools/Orkeon.Tools.csproj"
  "Orkeon.Tools.Embeddings.Local|src/packaging/Orkeon.Tools.Embeddings.Local/Orkeon.Tools.Embeddings.Local.Package.csproj"
  "Orkeon.Scripting|src/packaging/Orkeon.Scripting/Orkeon.Scripting.Package.csproj"
)

missing=0
for entry in "${PACKAGES[@]}"; do
  csproj="${entry#*|}"
  [[ -f "$ORKEON_SRC/$csproj" ]] || { echo "pack-orkeon-feed: $ORKEON_SRC has no $csproj" >&2; missing=1; }
done
[[ $missing -eq 0 ]] || exit 1

mkdir -p "$FEED"
for entry in "${PACKAGES[@]}"; do
  id="${entry%%|*}"
  csproj="${entry#*|}"
  echo "==> pack $id $VERSION"
  dotnet pack "$ORKEON_SRC/$csproj" -c Release -p:Version="$VERSION" -o "$FEED" --nologo -v quiet
  [[ -f "$FEED/$id.$VERSION.nupkg" ]] || { echo "pack-orkeon-feed: $FEED/$id.$VERSION.nupkg was not produced" >&2; exit 1; }
done
