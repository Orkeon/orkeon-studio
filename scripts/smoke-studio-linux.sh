#!/usr/bin/env bash
# Smokes the Linux archive scripts/package-studio.sh produces: extracts it, checks both
# Terminal.Gui launchers are there and executable, and that each answers `--version` with
# no terminal at all (stdin from /dev/null, both streams to a file) by a line starting
# with its own name. That is the headless contract: a Terminal.Gui app that initialised a
# console driver before handling --version would die here rather than in an ssh session.
# Adapted from Orkeon's smoke_step_studio_present (scripts/smoke-onboarding/lib/smoke-common.sh).
#
# When an expected version is given (the tag without its leading v), the version each launcher
# reports must equal it, not merely be present.
#
# Usage: scripts/smoke-studio-linux.sh <orkeon-studio-*-linux-x64.tar.gz> [work-dir] [expected-version]
set -euo pipefail

ARCHIVE="${1:?usage: smoke-studio-linux.sh <archive.tar.gz> [work-dir] [expected-version]}"
WORK="${2:-$(mktemp -d)}"
EXPECTED_VERSION="${3:-}"
APPS="orkeon-studio-config orkeon-studio-run"

mkdir -p "$WORK/extract" "$WORK/logs"
tar -xzf "$ARCHIVE" -C "$WORK/extract"
mapfile -t roots < <(find "$WORK/extract" -mindepth 1 -maxdepth 1 -type d)
[[ ${#roots[@]} -eq 1 ]] || { echo "FAIL archive: expected one top-level folder, found ${#roots[@]}" >&2; exit 1; }
BIN="${roots[0]}/bin"

failed=0
for app in $APPS; do
  path="$BIN/$app"
  if [[ ! -x "$path" ]]; then
    echo "FAIL studio-launchers: $path missing or not executable"
    failed=1
    continue
  fi
  out="$WORK/logs/$app.version.out"
  err="$WORK/logs/$app.version.err"
  ec=0
  ( cd "$WORK" && "$path" --version ) </dev/null >"$out" 2>"$err" || ec=$?
  if [[ $ec -ne 0 ]]; then
    tail -n 10 "$err" | sed 's/^/    | /'
    echo "FAIL studio-version: $app --version exited $ec (expected 0, headless)"
    failed=1
  elif [[ -n "$EXPECTED_VERSION" ]] && [[ "$(head -n1 "$out" | awk '{print $2}')" != "$EXPECTED_VERSION" ]]; then
    head -n 5 "$out" | sed 's/^/    | /'
    echo "FAIL studio-version: $app reported '$(head -n1 "$out")', expected version $EXPECTED_VERSION"
    failed=1
  elif grep -q "^$app " "$out"; then
    echo "PASS studio-version: $(head -n1 "$out") (no TTY)"
  else
    head -n 5 "$out" | sed 's/^/    | /'
    echo "FAIL studio-version: $app --version exited 0 but printed no '$app <version>' line"
    failed=1
  fi
done

for app in orkeon-studio orkeon-studio.cmd; do
  if [[ -e "$BIN/$app" ]]; then
    echo "FAIL studio-wpf: $BIN/$app is the Windows app, it has no place in a Linux archive"
    failed=1
  fi
done

exit "$failed"
