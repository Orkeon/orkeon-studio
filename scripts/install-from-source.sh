#!/usr/bin/env bash
# Installs Orkeon Studio from this clone, next to an Orkeon CLI (Linux and macOS;
# scripts/install-from-source.ps1 is the same contract for Windows).
#
#   git clone https://github.com/Orkeon/orkeon.git
#   git clone https://github.com/Orkeon/orkeon-studio.git
#   cd orkeon-studio
#   ./scripts/install-from-source.sh --with-orkeon
#
# What it does, in this order:
#   1. checks what the build needs -- every missing tool is named at once, before
#      anything is compiled -- and finds the Orkeon checkout (--orkeon-src, else
#      ORKEON_SOURCE_ROOT, else ../orkeon);
#   2. with --with-orkeon, installs the Orkeon CLI from that checkout first, with the
#      checkout's own scripts/install-from-source.sh;
#   3. packs the Orkeon packages the Studio apps compile against into orkeon-feed/, at a
#      version dated by the checkout's last commit (scripts/resolve-version.sh), unless
#      the feed already holds them: a new Orkeon commit gives a new version, and no cache
#      is ever cleared. Two commits of the same minute share a version, and NuGet keeps
#      the first copy it extracted: the script refuses to pack a checkout with uncommitted
#      changes, or to reuse a feed packed from another commit;
#   4. publishes the Studio apps of this machine -- orkeon-studio-config and
#      orkeon-studio-run -- into <prefix>/lib/orkeon/libexec, next to the CLI, where
#      Studio looks for it first, and links their launchers into <prefix>/bin.
#
# Studio lives inside the CLI's installation, and the CLI's install.sh replaces that
# directory whole: after reinstalling the CLI, run this script again. With --with-orkeon,
# the CLI's own installer also publishes the Studio copy its checkout carries, which
# this script then replaces. Each Orkeon commit packed adds its packages to orkeon-feed/
# and to the NuGet packages folder.
#
# Usage:
#   scripts/install-from-source.sh [--orkeon-src DIR] [--with-orkeon] [--prefix DIR]
#                                  [--modify-path] [--self-contained] [--uninstall]
#                                  [--dry-run]
#
# --orkeon-src      the Orkeon checkout to pack (and, with --with-orkeon, to install).
# --with-orkeon     installs the Orkeon CLI from --orkeon-src before Studio.
# --prefix          where the CLI is installed. Default: ~/.local (no sudo needed).
# --modify-path     passed to the CLI's install.sh (--with-orkeon only): appends
#                   <prefix>/bin to your shell rc files.
# --self-contained  bundles the .NET runtime with each app (the first run downloads the
#                   runtime pack once). Default: the apps use the .NET 10 runtime of the
#                   machine, which the SDK this build needs already provides -- found in
#                   the system location, or through DOTNET_ROOT for an SDK installed
#                   elsewhere (~/.dotnet, say): the script warns when neither holds it.
# --uninstall       removes the Studio apps this script installed under --prefix and
#                   leaves the CLI. Builds nothing.
# --dry-run         checks the prerequisites, prints the versions and the commands it
#                   would run, and stops there.
#
# Exit: 0 installed (or would be, with --dry-run); 1 a prerequisite is missing, the
# build or the install failed, or there is nothing to uninstall; 2 on a usage error.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

ORKEON_SRC="${ORKEON_SOURCE_ROOT:-$REPO_ROOT/../orkeon}"
WITH_ORKEON=0
PREFIX="${HOME}/.local"
MODIFY_PATH=0
SELF_CONTAINED=false
UNINSTALL=0
DRY_RUN=0

usage() { echo "install-from-source: $*" >&2; exit 2; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --orkeon-src)     [[ $# -ge 2 ]] || usage "--orkeon-src takes a directory"; ORKEON_SRC="$2"; shift 2 ;;
    --orkeon-src=*)   ORKEON_SRC="${1#--orkeon-src=}"; shift ;;
    --with-orkeon)    WITH_ORKEON=1; shift ;;
    --prefix)         [[ $# -ge 2 ]] || usage "--prefix takes a directory"; PREFIX="$2"; shift 2 ;;
    --prefix=*)       PREFIX="${1#--prefix=}"; shift ;;
    --modify-path)    MODIFY_PATH=1; shift ;;
    --self-contained) SELF_CONTAINED=true; shift ;;
    --uninstall)      UNINSTALL=1; shift ;;
    --dry-run)        DRY_RUN=1; shift ;;
    -h|--help)        sed -n "2,$(( $(grep -n '^set -euo pipefail' "$0" | cut -d: -f1) - 1 ))p" "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) usage "unknown argument $1" ;;
  esac
done

[[ "$MODIFY_PATH" -eq 0 || "$WITH_ORKEON" -eq 1 ]] \
  || usage "--modify-path goes with --with-orkeon: the CLI's installer is what edits the PATH"

[[ -n "$PREFIX" ]] || usage "--prefix takes a directory"
# Absolute, so the launcher links point at it from anywhere.
[[ "$PREFIX" == /* ]] || PREFIX="$PWD/$PREFIX"
LIB_DIR="$PREFIX/lib/orkeon"
BIN_DIR="$PREFIX/bin"
FEED="$REPO_ROOT/orkeon-feed"

# name|project|apphost: what this script installs. The WPF app is Windows-only
# (install-from-source.ps1).
APPS=(
  "orkeon-studio-config|src/apps/Orkeon.Studio.Config/Orkeon.Studio.Config.csproj|Orkeon.Studio.Config"
  "orkeon-studio-run|src/apps/Orkeon.Studio.Run/Orkeon.Studio.Run.csproj|Orkeon.Studio.Run"
)

# The Orkeon packages the apps restore (Directory.Packages.props, src/Directory.Build.props
# for the analyzer) and the project each is packed from; their Orkeon.* dependencies are
# among them. Orkeon.Tools and Orkeon.Scripting serve the tests alone. The same projects
# Orkeon's publish.yml packs: Orkeon.Cli.TerminalGui through its wrapper, which depends
# on the Orkeon package instead of shipping its assemblies a second time.
PACKAGES=(
  "Orkeon|src/packaging/Orkeon/Orkeon.csproj"
  "Orkeon.Constants.Cli|src/constants/Orkeon.Constants.Cli/Orkeon.Constants.Cli.csproj"
  "Orkeon.Cli.Abstractions|src/cli/Orkeon.Cli.Abstractions/Orkeon.Cli.Abstractions.csproj"
  "Orkeon.Cli.TerminalGui|src/packaging/Orkeon.Cli.TerminalGui/Orkeon.Cli.TerminalGui.Package.csproj"
  "Orkeon.Compliance.Vfs|src/analyzers/Orkeon.Compliance.Vfs/Orkeon.Compliance.Vfs.csproj"
)

step() { printf '\n==> %s\n' "$*"; }

# Prints a command as a shell would read it back.
show() { printf ' '; printf ' %q' "$@"; printf '\n'; }

LAUNCHER_MARK="written by orkeon-studio's scripts/install-from-source.sh"

# Removes one app: its launcher symlink in $BIN_DIR (only when it points into $LIB_DIR),
# its wrapper and its published directory.
remove_app() { # $1=app name
  local link="$BIN_DIR/$1"
  if [[ -h "$link" ]]; then
    case "$(readlink "$link")" in "$LIB_DIR"/*) rm -f "$link" ;; esac
  fi
  rm -rf "$LIB_DIR/bin/$1" "$LIB_DIR/libexec/$1"
}

# --- Uninstall: nothing to build -------------------------------------------------
# Only what this script wrote goes: the CLI's archives carry a Studio of their own, whose
# launcher does not bear this script's mark. A link left dangling by a CLI reinstall goes too.
if [[ "$UNINSTALL" -eq 1 ]]; then
  found=0
  for entry in "${APPS[@]}"; do
    name="${entry%%|*}"
    if grep -qF "$LAUNCHER_MARK" "$LIB_DIR/bin/$name" 2>/dev/null; then
      found=1
      if [[ "$DRY_RUN" -eq 1 ]]; then echo "Would remove $name from $LIB_DIR"; else remove_app "$name"; fi
    elif [[ -h "$BIN_DIR/$name" && ! -e "$BIN_DIR/$name" ]]; then
      case "$(readlink "$BIN_DIR/$name")" in
        "$LIB_DIR"/*)
          found=1
          if [[ "$DRY_RUN" -eq 1 ]]; then echo "Would remove the dangling link $BIN_DIR/$name"; else rm -f "$BIN_DIR/$name"; fi ;;
      esac
    fi
  done
  if [[ "$found" -eq 0 ]]; then
    echo "Nothing to uninstall: no Orkeon Studio app installed by this script under $LIB_DIR." >&2
    exit 1
  fi
  [[ "$DRY_RUN" -eq 1 ]] || { rm -f "$LIB_DIR/STUDIO-VERSION"; echo "Orkeon Studio uninstalled from $LIB_DIR (the CLI stays)."; }
  exit 0
fi

# --- The machine this installs for -----------------------------------------------
case "$(uname -s)" in
  Linux)  os="linux" ;;
  Darwin) os="osx" ;;
  *)
    echo "install-from-source: no Studio build for '$(uname -s)'. On Windows, run scripts\\install-from-source.ps1 from PowerShell 7." >&2
    exit 1 ;;
esac
case "$(uname -m)" in
  x86_64|amd64)  arch="x64" ;;
  aarch64|arm64) arch="arm64" ;;
  *)
    echo "install-from-source: no Studio build for the '$(uname -m)' architecture (x64 and arm64 are)." >&2
    exit 1 ;;
esac
RID="$os-$arch"

# --- Prerequisites, all of them, before anything is compiled -----------------------
SDK_WANTED="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$REPO_ROOT/global.json" 2>/dev/null | head -n1)" || true
SDK_WANTED="${SDK_WANTED:-10.0.300}"
SDK_LINE="${SDK_WANTED%.*}"

missing=()
if ! command -v dotnet >/dev/null 2>&1; then
  missing+=("the .NET SDK $SDK_WANTED or a later $SDK_LINE.x (global.json pins it) -- https://dotnet.microsoft.com/download/dotnet/$SDK_LINE")
elif ! (cd "$REPO_ROOT" && dotnet --version >/dev/null 2>&1); then
  have="$( (cd / && dotnet --list-sdks 2>/dev/null) | sed 's/ .*//' | tr '\n' ' ' | sed 's/ $//')"
  missing+=("a .NET SDK that satisfies global.json: $SDK_WANTED or a later $SDK_LINE.x (installed: ${have:-none}) -- https://dotnet.microsoft.com/download/dotnet/$SDK_LINE")
fi
command -v git >/dev/null 2>&1 \
  || missing+=("git, which dates the versions of this build -- https://git-scm.com/downloads")
if [[ ! -f "$ORKEON_SRC/src/packaging/Orkeon/Orkeon.csproj" ]]; then
  missing+=("an Orkeon checkout at $ORKEON_SRC (--orkeon-src or ORKEON_SOURCE_ROOT name another): git clone --depth 1 --filter=blob:none https://github.com/Orkeon/orkeon.git")
elif [[ ! -f "$ORKEON_SRC/src/packaging/Orkeon.Cli.TerminalGui/Orkeon.Cli.TerminalGui.Package.csproj" ]]; then
  # Before the packaging that ships the CLI family to NuGet, three of the packages Studio
  # needs cannot be packed at all.
  missing+=("an Orkeon checkout that packages the CLI family for Studio (src/packaging/Orkeon.Cli.TerminalGui): $ORKEON_SRC predates it -- check out a commit that has it")
fi
if [[ "$WITH_ORKEON" -eq 0 && ! -x "$LIB_DIR/libexec/orkeon/orkeon" ]]; then
  missing+=("an Orkeon CLI installed under $PREFIX ($LIB_DIR/libexec/orkeon/orkeon): add --with-orkeon to install it from $ORKEON_SRC first")
fi

if [[ ${#missing[@]} -gt 0 ]]; then
  {
    echo "install-from-source: ${#missing[@]} thing(s) the build needs are missing. Nothing was built."
    for item in "${missing[@]}"; do echo "  - $item"; done
    echo "Provide what is listed, then run this script again. It installs none of it for you."
  } >&2
  exit 1
fi
ORKEON_SRC="$(cd "$ORKEON_SRC" && pwd)"

# --- Versions ----------------------------------------------------------------------
STUDIO_VERSION="$(bash "$REPO_ROOT/scripts/resolve-version.sh" --props "$REPO_ROOT/src/Directory.Build.props")" || STUDIO_VERSION=""
ORKEON_VERSION="$(bash "$ORKEON_SRC/scripts/resolve-version.sh" --props "$ORKEON_SRC/src/Directory.Build.props")" || ORKEON_VERSION=""
if [[ -z "$STUDIO_VERSION" || -z "$ORKEON_VERSION" ]]; then
  echo "install-from-source: resolve-version.sh gave no version (see its message above)." >&2
  exit 1
fi

# The feed is complete when every package is there at this version.
feed_complete() {
  local entry
  for entry in "${PACKAGES[@]}"; do
    [[ -f "$FEED/${entry%%|*}.$ORKEON_VERSION.nupkg" ]] || return 1
  done
}

echo "Orkeon Studio $STUDIO_VERSION, from the sources in $REPO_ROOT ($RID),"
echo "against Orkeon $ORKEON_VERSION from $ORKEON_SRC."

# The version is the minute of the Orkeon commit: uncommitted changes, or another commit
# of the same minute, do not move it, and NuGet never extracts a version twice. The feed
# records the commit it was packed from, and a pack only ever sees a committed tree.
ORKEON_HEAD="$(git -C "$ORKEON_SRC" rev-parse HEAD 2>/dev/null)" || ORKEON_HEAD=""
FEED_MARK="$FEED/.orkeon-$ORKEON_VERSION.commit"
ORKEON_DIRTY=0
[[ -z "$(git -C "$ORKEON_SRC" status --porcelain --untracked-files=no --ignore-submodules 2>/dev/null)" ]] || ORKEON_DIRTY=1
if feed_complete; then
  packed_from="$(cat "$FEED_MARK" 2>/dev/null)" || packed_from=""
  if [[ -n "$packed_from" && -n "$ORKEON_HEAD" && "$packed_from" != "$ORKEON_HEAD" ]]; then
    echo "install-from-source: orkeon-feed/ holds Orkeon $ORKEON_VERSION packed from commit $packed_from," >&2
    echo "  but $ORKEON_SRC is at $ORKEON_HEAD, which carries the same version. Commit again (a new" >&2
    echo "  minute gives a new version), or check out $packed_from. Nothing was built." >&2
    exit 1
  fi
  [[ "$ORKEON_DIRTY" -eq 0 ]] \
    || echo "Note: $ORKEON_SRC has uncommitted changes; this build uses the packages of its last commit, without them."
elif [[ "$ORKEON_DIRTY" -eq 1 ]]; then
  echo "install-from-source: $ORKEON_SRC has uncommitted changes. Packed, they would carry the version" >&2
  echo "  of its last commit, $ORKEON_VERSION, for good. Commit or stash them, then run this again." >&2
  exit 1
fi

ORKEON_INSTALL=(bash "$ORKEON_SRC/scripts/install-from-source.sh" --prefix "$PREFIX")
[[ "$MODIFY_PATH" -eq 1 ]] && ORKEON_INSTALL+=(--modify-path)
# NU1603 is the restore settling for a higher version than the one asked when that one is
# missing: 1.0.0-studio.local sorts above every 1.0.0-rc.N.local.* stamp, so that would be
# a Studio built against the wrong Orkeon.
VERSION_PROPS=(-p:OrkeonVersion="$ORKEON_VERSION" -p:NuGetAudit=false -warnaserror:NU1603)
STAGE="$REPO_ROOT/artifacts/install-from-source/$RID"
# StudioBuildRoot (Directory.Build.props) keeps this build's obj/ and bin/ under $STAGE:
# restored with another OrkeonVersion, it must not leave its assets file where a plain
# `dotnet build` of the checkout that skips the restore would pick it up. The Orkeon
# packages may bring one file twice, as the Orkeon installers' own publish of Studio allows.
PUBLISH_PROPS=(-p:StudioBuildRoot="$STAGE/build" -p:PublishTrimmed=false -p:ErrorOnDuplicatePublishOutputFiles=false)

if [[ "$DRY_RUN" -eq 1 ]]; then
  echo "Prerequisites: all present. --dry-run: nothing is built, nothing is installed. Would run:"
  [[ "$WITH_ORKEON" -eq 0 ]] || show "${ORKEON_INSTALL[@]}"
  if feed_complete; then
    echo "  (orkeon-feed/ already holds the packages at $ORKEON_VERSION: no pack)"
  else
    for entry in "${PACKAGES[@]}"; do
      IFS='|' read -r id csproj <<<"$entry"
      show dotnet pack "$ORKEON_SRC/$csproj" -c Release -p:Version="$ORKEON_VERSION" -p:NuGetAudit=false -o "$FEED" --nologo -v quiet
    done
  fi
  for entry in "${APPS[@]}"; do
    IFS='|' read -r name csproj apphost <<<"$entry"
    show dotnet publish "$REPO_ROOT/$csproj" -c Release -r "$RID" --self-contained "$SELF_CONTAINED" \
      -p:Version="$STUDIO_VERSION" "${VERSION_PROPS[@]}" "${PUBLISH_PROPS[@]}" -o "$STAGE/$name" --nologo -v quiet
    echo "    then copied to $LIB_DIR/libexec/$name, launcher linked as $BIN_DIR/$name"
  done
  exit 0
fi

# --- The CLI -------------------------------------------------------------------------
if [[ "$WITH_ORKEON" -eq 1 ]]; then
  step "Installing the Orkeon CLI from $ORKEON_SRC"
  if ! "${ORKEON_INSTALL[@]}"; then
    echo "install-from-source: the Orkeon CLI did not install (its message is above). Studio was not built." >&2
    exit 1
  fi
  if [[ ! -x "$LIB_DIR/libexec/orkeon/orkeon" ]]; then
    echo "install-from-source: the Orkeon install ended without $LIB_DIR/libexec/orkeon/orkeon." >&2
    exit 1
  fi
fi

# --- The feed ------------------------------------------------------------------------
if feed_complete; then
  step "orkeon-feed/ already holds the Orkeon packages at $ORKEON_VERSION"
else
  step "Packing the Orkeon packages at $ORKEON_VERSION into orkeon-feed/"
  mkdir -p "$FEED"
  for entry in "${PACKAGES[@]}"; do
    IFS='|' read -r id csproj <<<"$entry"
    echo "    pack $id"
    if ! dotnet pack "$ORKEON_SRC/$csproj" -c Release -p:Version="$ORKEON_VERSION" \
           -p:NuGetAudit=false -o "$FEED" --nologo -v quiet; then
      echo "install-from-source: packing $id failed (its message is above). Nothing was installed." >&2
      exit 1
    fi
    # A project that is not packable packs nothing and still succeeds: say it here, not as
    # a restore that quietly settles for another version of the package.
    if [[ ! -f "$FEED/$id.$ORKEON_VERSION.nupkg" ]]; then
      echo "install-from-source: packing $id wrote no $id.$ORKEON_VERSION.nupkg into $FEED." >&2
      exit 1
    fi
  done
  [[ -z "$ORKEON_HEAD" ]] || printf '%s\n' "$ORKEON_HEAD" > "$FEED_MARK"
fi

# --- Studio --------------------------------------------------------------------------
rm -rf "$STAGE"
mkdir -p "$STAGE"

step "Publishing Studio for $RID (self-contained: $SELF_CONTAINED)"
for entry in "${APPS[@]}"; do
  IFS='|' read -r name csproj apphost <<<"$entry"
  echo "    publish $name"
  # $STAGE was just emptied: the restore starts from no earlier resolution.
  if ! dotnet publish "$REPO_ROOT/$csproj" -c Release -r "$RID" --self-contained "$SELF_CONTAINED" \
         -p:Version="$STUDIO_VERSION" "${VERSION_PROPS[@]}" "${PUBLISH_PROPS[@]}" \
         -o "$STAGE/$name" --nologo -v quiet; then
    echo "install-from-source: publishing $name failed (its message is above). Nothing was installed." >&2
    exit 1
  fi
done

step "Installing to $LIB_DIR"
mkdir -p "$LIB_DIR/bin" "$LIB_DIR/libexec" "$BIN_DIR"
linked=""
for entry in "${APPS[@]}"; do
  IFS='|' read -r name csproj apphost <<<"$entry"
  remove_app "$name"
  cp -R "$STAGE/$name" "$LIB_DIR/libexec/$name"
  chmod +x "$LIB_DIR/libexec/$name/$apphost"
  # The same launcher the Orkeon archives carry (scripts/installer-assets/wrapper.sh.tmpl
  # there): resolves its real place through the symlink, then execs the apphost.
  cat > "$LIB_DIR/bin/$name" <<EOF
#!/bin/sh
# Orkeon Studio launcher for $name -- $LAUNCHER_MARK.
prg="\$0"
while [ -h "\$prg" ]; do
  link=\$(readlink "\$prg")
  case "\$link" in
    /*) prg="\$link" ;;
    *)  prg="\$(dirname "\$prg")/\$link" ;;
  esac
done
here=\$(cd "\$(dirname "\$prg")" && pwd)
root=\$(dirname "\$here")
if [ -z "\${ORKEON_ESBUILD_PATH:-}" ] && [ -x "\$root/libexec/esbuild-bin/esbuild" ]; then
  ORKEON_ESBUILD_PATH="\$root/libexec/esbuild-bin/esbuild"
  export ORKEON_ESBUILD_PATH
fi
exec "\$root/libexec/$name/$apphost" "\$@"
EOF
  chmod +x "$LIB_DIR/bin/$name"
  ln -sf "$LIB_DIR/bin/$name" "$BIN_DIR/$name"
  linked="$linked$name "
done
printf '%s\n' "$STUDIO_VERSION" > "$LIB_DIR/STUDIO-VERSION"
rm -rf "$STAGE"

# macOS: the same filet as the CLI's install.sh. Apple Silicon refuses to load an unsigned
# Mach-O, and the native libraries the Orkeon packages bring arrive signed or not; only
# what codesign rejects is signed ad hoc, a stubborn file is a warning.
if [[ "$os" == "osx" ]]; then
  for entry in "${APPS[@]}"; do
    dir="$LIB_DIR/libexec/${entry%%|*}"
    if command -v xattr >/dev/null 2>&1; then xattr -dr com.apple.quarantine "$dir" 2>/dev/null || true; fi
    command -v codesign >/dev/null 2>&1 || continue
    while IFS= read -r f; do
      case "$f" in
        *.dylib) ;;
        *) [[ -x "$f" ]] || continue
           if command -v file >/dev/null 2>&1 && ! file "$f" 2>/dev/null | grep -q 'Mach-O'; then continue; fi ;;
      esac
      codesign -v "$f" >/dev/null 2>&1 || codesign --force -s - "$f" >/dev/null 2>&1 \
        || echo "WARNING: could not ad-hoc sign $f -- it may fail to load." >&2
    done < <(find "$dir" -type f)
  done
fi

echo
echo "Orkeon Studio $STUDIO_VERSION is installed in $LIB_DIR, next to the Orkeon CLI."
echo "Commands linked in $BIN_DIR: $linked"
echo "After reinstalling the CLI, or a 'git pull' here, run this script again."

# A framework-dependent apphost looks for .NET in DOTNET_ROOT or the system location,
# never on PATH: an SDK installed in ~/.dotnet alone leaves the apps unable to start.
if [[ "$SELF_CONTAINED" == "false" && -z "${DOTNET_ROOT:-}" ]]; then
  runtime_found=0
  for root in /usr/share/dotnet /usr/lib/dotnet /usr/local/share/dotnet /usr/lib64/dotnet \
              "$(cat /etc/dotnet/install_location 2>/dev/null || true)"; do
    if [[ -n "$root" ]] && compgen -G "$root/shared/Microsoft.NETCore.App/10.*" >/dev/null; then runtime_found=1; fi
  done
  if [[ "$runtime_found" -eq 0 ]]; then
    echo "Warning: no .NET 10 runtime in the system location, and DOTNET_ROOT is not set. Export" >&2
    echo "  DOTNET_ROOT=$(dirname "$(command -v dotnet)") in your shell profile, or reinstall with --self-contained." >&2
  fi
fi
