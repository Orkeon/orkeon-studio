#!/usr/bin/env bash
# Builds the Orkeon Studio release archives, one per RID, from this checkout.
#
# Usage:
#   scripts/package-studio.sh [--version X.Y.Z[-suffix]] [--rids "win-x64 linux-x64"]
#                             [--out artifacts/release] [-c Release] [--no-restore]
#
# What each RID carries (the same split Orkeon's own installers used for Studio):
#   win-x64    orkeon-studio-<ver>-win-x64.zip     the WPF app (`orkeon-studio`)
#   linux-x64  orkeon-studio-<ver>-linux-x64.tar.gz the two Terminal.Gui apps
#                                                   (`orkeon-studio-config`, `orkeon-studio-run`)
# Every app is published self-contained (the .NET runtime is bundled, no SDK or runtime
# needed on the target), untrimmed and not single-file, into libexec/<app>/, with a
# launcher in bin/. The orkeon CLI is NOT bundled: Studio finds it through ORKEON_CLI_DIR
# or PATH (OrkeonBinaryLocator). The win-x64 zip is also the tree scripts/build-msi.ps1
# harvests into the MSI.
#
# A single Linux machine publishes both RIDs: the WPF project sets EnableWindowsTargeting,
# and a self-contained publish compiles nothing native (the apphost and the runtime come
# from the SDK's runtime packs, which the restore fetches per RID).
#
# Version: --version, else <VersionPrefix>[-<VersionSuffix>] of src/Directory.Build.props.
# --no-restore passes --no-restore to every publish: restore each project first with the
# same RID and `-p:SelfContained=true` (an offline local run does that).
#
# Requires python3 (reads the runtime packs a publish bundles to copy their notices).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

VERSION=""
RIDS="win-x64 linux-x64"
OUT="$REPO_ROOT/artifacts/release"
CONFIG="Release"
RESTORE_ARG=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --rids)    RIDS="$2"; shift 2 ;;
    --out)     OUT="$2"; shift 2 ;;
    --no-restore) RESTORE_ARG="--no-restore"; shift ;;
    -c|--configuration) CONFIG="$2"; shift 2 ;;
    -h|--help) sed -n '2,25p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

command -v python3 >/dev/null 2>&1 || {
  echo "python3 is required: it copies the license and notices of the bundled .NET runtime." >&2
  exit 1
}

# --- Version -------------------------------------------------------------------
if [[ -z "$VERSION" ]]; then
  props="$REPO_ROOT/src/Directory.Build.props"
  prefix="$(sed -n 's/.*<VersionPrefix>\(.*\)<\/VersionPrefix>.*/\1/p' "$props")"
  suffix="$(sed -n 's/.*<VersionSuffix>\(.*\)<\/VersionSuffix>.*/\1/p' "$props")"
  VERSION="$prefix${suffix:+-$suffix}"
fi
[[ -n "$VERSION" ]] || { echo "Could not resolve a version; pass --version." >&2; exit 1; }

# --- App table: name | csproj (repo-relative) | apphost assembly name ---------------
APPS=(
  "orkeon-studio|src/apps/Orkeon.Studio.Wpf/Orkeon.Studio.Wpf.csproj|Orkeon.Studio"
  "orkeon-studio-config|src/apps/Orkeon.Studio.Config/Orkeon.Studio.Config.csproj|Orkeon.Studio.Config"
  "orkeon-studio-run|src/apps/Orkeon.Studio.Run/Orkeon.Studio.Run.csproj|Orkeon.Studio.Run"
)

# WPF exists on Windows only; the Terminal.Gui apps ship for Linux (on Windows the WPF
# app covers both of their roles).
app_for_rid() { # $1=app-name $2=rid
  case "$1" in
    orkeon-studio) [[ "$2" == win-* ]] ;;
    orkeon-studio-config|orkeon-studio-run) [[ "$2" == linux-* ]] ;;
    *) return 1 ;;
  esac
}

for rid in $RIDS; do
  case "$rid" in
    win-x64|linux-x64) ;;
    *) echo "Unsupported RID '$rid' (expected: win-x64, linux-x64)" >&2; exit 2 ;;
  esac
done

mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
STAGE="$OUT/_stage"
mkdir -p "$STAGE"

echo "==> Packaging Orkeon Studio $VERSION for: $RIDS"

# Copies, byte for byte, the license and notice files at the root of every runtime pack a
# self-contained publish bundles (the `runtimepack` libraries of its *.deps.json, plus the
# frameworks its runtimeconfig marks as included) to <to>/<pack>/. The packs are looked up
# in the restore folders: NUGET_PACKAGES, <repo>/packages (RestorePackagesPath on Linux),
# ~/.nuget/packages. A pack missing, or without notices, stops the packaging.
copy_runtime_notices() { # $1=published-dir $2=licenses-dir
  python3 - "$1" "$2" "${NUGET_PACKAGES:-}" "$REPO_ROOT/packages" "$HOME/.nuget/packages" <<'PY'
import json, re, shutil, sys
from pathlib import Path

publish, to = Path(sys.argv[1]), Path(sys.argv[2])
folders = [Path(f) for f in sys.argv[3:] if f]
pack_re = re.compile(r"^(runtimepack\.)?(.+\.Runtime\.[^/]+)/(.+)$")
packs, rid = {}, ""
for deps in sorted(publish.glob("*.deps.json")):
    data = json.loads(deps.read_text(encoding="utf-8-sig"))
    rid = rid or ((data.get("runtimeTarget") or {}).get("name") or "").partition("/")[2]
    for key, lib in (data.get("libraries") or {}).items():
        m = pack_re.match(key)
        if m and lib.get("type") == "runtimepack":
            packs.setdefault(m.group(2).lower(), (m.group(2), m.group(3)))
for cfg in sorted(publish.glob("*.runtimeconfig.json")):
    opts = json.loads(cfg.read_text(encoding="utf-8-sig")).get("runtimeOptions") or {}
    for fw in opts.get("includedFrameworks") or []:
        pack = f"{fw.get('name')}.Runtime.{rid}"
        packs.setdefault(pack.lower(), (pack, str(fw.get("version"))))
if not packs:
    sys.exit(f"{publish}: no runtime pack found -- the publish is not self-contained")
notice = re.compile(r"^(LICENSE|THIRD-PARTY-NOTICES)(\.[A-Za-z]+)?$", re.IGNORECASE)
for pack, version in sorted(packs.values()):
    base = next((f / pack.lower() / version.lower() for f in folders
                 if (f / pack.lower() / version.lower()).is_dir()), None)
    if base is None:
        sys.exit(f"{pack} {version}: in no package folder ({', '.join(map(str, folders))})")
    names = sorted(p.name for p in base.iterdir() if p.is_file() and notice.match(p.name))
    if not names:
        sys.exit(f"{pack} {version}: no license or notices file in {base}")
    target = to / pack
    target.mkdir(parents=True, exist_ok=True)
    for name in names:
        shutil.copyfile(base / name, target / name)
    print(f"    notices {pack} {version}: {', '.join(names)}")
PY
}

# Hardlinks identical files of a tree so GNU tar stores each payload once: the two
# self-contained Linux apps carry byte-identical copies of the .NET runtime and of the
# Orkeon assemblies. Transparent at extraction. Zip has no hard links: Linux only.
hardlink_dedup() { # $1=root
  find "$1" -type f -size +4k -print0 \
    | xargs -0 sha256sum \
    | LC_ALL=C sort \
    | awk '{ hash=$1; $1=""; sub(/^  ?/,""); path=$0 }
           hash==prev { printf "%s\t%s\n", keep, path; next }
           { prev=hash; keep=path }' \
    | { linked=0
        while IFS=$'\t' read -r keep dup; do
          [[ -n "$dup" && ! "$keep" -ef "$dup" ]] || continue
          ln -f "$keep" "$dup"
          linked=$((linked + 1))
        done
        echo "    hardlink-dedup: $linked file(s) linked"; }
}

make_zip() { # $1=parent-dir $2=folder-name $3=zip-path
  if command -v zip >/dev/null 2>&1; then
    (cd "$1" && zip -rq "$3" "$2")
  else
    (cd "$1" && python3 -c "
import shutil, sys
shutil.make_archive(sys.argv[1].removesuffix('.zip'), 'zip', '.', sys.argv[2])
" "$3" "$2")
  fi
}

write_launcher() { # $1=rid $2=app $3=apphost $4=bin-dir
  if [[ "$1" == win-* ]]; then
    # cmd.exe wants CRLF.
    printf '%s\r\n' \
      '@echo off' \
      "rem Orkeon Studio launcher for $2 -- generated by scripts/package-studio.sh." \
      'setlocal' \
      'set "ROOT=%~dp0.."' \
      "\"%ROOT%\\libexec\\$2\\$3.exe\" %*" \
      'endlocal & exit /b %errorlevel%' > "$4/$2.cmd"
  else
    cat > "$4/$2" <<EOF
#!/bin/sh
# Orkeon Studio launcher for $2 -- generated by scripts/package-studio.sh.
# Resolves its real location through install symlinks, then execs the apphost.
prg="\$0"
while [ -h "\$prg" ]; do
  link=\$(readlink "\$prg")
  case "\$link" in
    /*) prg="\$link" ;;
    *)  prg="\$(dirname "\$prg")/\$link" ;;
  esac
done
root=\$(dirname "\$(cd "\$(dirname "\$prg")" && pwd)")
exec "\$root/libexec/$2/$3" "\$@"
EOF
    chmod +x "$4/$2"
  fi
}

write_readme() { # $1=rid $2=dest
  local launchers
  if [[ "$1" == win-* ]]; then
    launchers='- `bin\orkeon-studio.cmd`: the desktop app (`libexec\orkeon-studio\Orkeon.Studio.exe`).'
  else
    launchers='- `bin/orkeon-studio-config`: the terminal settings editor.
- `bin/orkeon-studio-run`: the terminal crew launcher.'
  fi
  cat > "$2" <<EOF
# Orkeon Studio $VERSION ($1)

Front-ends over the Orkeon agent framework: https://github.com/Orkeon/orkeon-studio

$launchers

Each application is self-contained: no .NET runtime is needed. The license and notices of
the bundled .NET runtime are under \`licenses/\`.

## Prerequisite: the orkeon CLI

Studio never runs a crew itself: it drives the \`orkeon\` CLI, installed separately with
\`dotnet tool install -g Orkeon.Scripting.Cli\` or with the Orkeon installers
(https://github.com/Orkeon/orkeon/releases). Studio looks for it in the
\`ORKEON_CLI_DIR\` environment variable, then on \`PATH\`. On Windows it needs
\`orkeon.exe\` itself: the .NET tool puts one on \`PATH\`; for an Orkeon installer, set
\`ORKEON_CLI_DIR\` to its \`libexec\\orkeon\` folder.

## Install

Extract the archive anywhere and add its \`bin\` folder to \`PATH\`, or start a launcher
by its full path.
EOF
}

for RID in $RIDS; do
  PKG="orkeon-studio-$VERSION-$RID"
  ROOT="$STAGE/$PKG"
  rm -rf "$ROOT"
  mkdir -p "$ROOT/bin" "$ROOT/libexec" "$ROOT/licenses"
  echo "==> $RID"

  for entry in "${APPS[@]}"; do
    IFS='|' read -r name csproj apphost <<<"$entry"
    app_for_rid "$name" "$RID" || continue
    echo "    publish $name (self-contained)"
    # shellcheck disable=SC2086 # RESTORE_ARG is empty or one flag
    dotnet publish "$REPO_ROOT/$csproj" -c "$CONFIG" -r "$RID" $RESTORE_ARG \
      --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false \
      -p:Version="$VERSION" -p:ErrorOnDuplicatePublishOutputFiles=false \
      -o "$ROOT/libexec/$name" --nologo -v quiet
    copy_runtime_notices "$ROOT/libexec/$name" "$ROOT/licenses"
    write_launcher "$RID" "$name" "$apphost" "$ROOT/bin"
    [[ "$RID" == win-* ]] || chmod +x "$ROOT/libexec/$name/$apphost"
  done

  write_readme "$RID" "$ROOT/README.md"
  cp "$REPO_ROOT/LICENSE.md" "$REPO_ROOT/NOTICE.md" "$REPO_ROOT/THIRD-PARTY-NOTICES.md" "$ROOT/"
  printf '%s\n' "$VERSION" > "$ROOT/VERSION"

  if [[ "$RID" == win-* ]]; then
    rm -f "$OUT/$PKG.zip"
    make_zip "$STAGE" "$PKG" "$OUT/$PKG.zip"
    echo "    -> $OUT/$PKG.zip"
  else
    hardlink_dedup "$ROOT/libexec"
    tar -czf "$OUT/$PKG.tar.gz" -C "$STAGE" "$PKG"
    echo "    -> $OUT/$PKG.tar.gz"
  fi
done

# `|| true`: with a single RID one of the globs matches nothing.
(cd "$OUT" && { ls ./*.tar.gz ./*.zip 2>/dev/null || true; } | sed 's|^\./||' | xargs -r sha256sum > SHA256SUMS)
echo "==> Done. Artifacts in $OUT:"
(cd "$OUT" && ls -lh ./*.tar.gz ./*.zip SHA256SUMS 2>/dev/null || true)
