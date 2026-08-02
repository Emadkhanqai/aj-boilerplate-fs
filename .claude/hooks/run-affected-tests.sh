#!/usr/bin/env bash
# run-affected-tests.sh — PostToolUse(Edit|Write)
#
# Runs the tests affected by the file that was just edited, so a break is found
# in seconds rather than at the quality gate.
#
#   backend  .cs  -> dotnet test on the touched project if it is a test project,
#                    otherwise the fast unit-test project
#   frontend .ts  -> nx test on the Nx project that owns the file
#
# NON-BLOCKING BY DESIGN. It never exits 2. A failure exits 1, which surfaces the
# output to the agent as a warning without cancelling the edit.
#
# Silent when nothing is affected.
#
# Environment:
#   AJ_SKIP_TESTS_HOOK=1     skip entirely
#   AJ_HOOK_TEST_TIMEOUT=180 seconds before the run is abandoned (default 180)

set -u

PAYLOAD="$(cat 2>/dev/null || true)"

[ "${AJ_SKIP_TESTS_HOOK:-0}" = "1" ] && exit 0

TIMEOUT_S="${AJ_HOOK_TEST_TIMEOUT:-180}"

json_str() {
  _key="$1"
  [ -z "${PAYLOAD:-}" ] && return 0
  if command -v jq >/dev/null 2>&1; then
    printf '%s' "$PAYLOAD" | jq -r --arg k "$_key" \
      'getpath($k | split(".")) // empty' 2>/dev/null && return 0
  fi
  if command -v python3 >/dev/null 2>&1; then
    printf '%s' "$PAYLOAD" | python3 -c '
import sys, json
try:
    d = json.load(sys.stdin)
except Exception:
    sys.exit(0)
for k in sys.argv[1].split("."):
    if isinstance(d, dict) and k in d:
        d = d[k]
    else:
        sys.exit(0)
print(d if isinstance(d, str) else json.dumps(d))
' "$_key" 2>/dev/null && return 0
  fi
  _leaf="${_key##*.}"
  printf '%s' "$PAYLOAD" \
    | sed -n "s/.*\"${_leaf}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" \
    | head -n 1
}

FILE="$(json_str 'tool_input.file_path')"
[ -z "${FILE:-}" ] && exit 0
[ -f "$FILE" ] || exit 0

# Nothing to run for these.
case "$FILE" in
  */node_modules/*|*/bin/*|*/obj/*|*/dist/*|*/.git/*|*/Migrations/*|*/api-types/*) exit 0 ;;
esac

# Run with a wall-clock bound when 'timeout' is available; the hook must never
# hang a session waiting on a stuck test host.
run_bounded() {
  if command -v timeout >/dev/null 2>&1; then
    timeout "${TIMEOUT_S}s" "$@"
  elif command -v gtimeout >/dev/null 2>&1; then
    gtimeout "${TIMEOUT_S}s" "$@"
  else
    "$@"
  fi
}

find_up() {   # find_up <start-dir> <glob>  -> prints the first match going upward
  _d="$1"; _g="$2"
  while [ -n "$_d" ] && [ "$_d" != "/" ]; do
    _hit="$(find "$_d" -maxdepth 1 -name "$_g" -print 2>/dev/null | head -n 1)"
    [ -n "$_hit" ] && { printf '%s' "$_hit"; return 0; }
    _d="$(dirname "$_d")"
  done
  return 1
}

# ---------------------------------------------------------------------------
# Backend
# ---------------------------------------------------------------------------
run_dotnet() {
  command -v dotnet >/dev/null 2>&1 || {
    printf '[run-affected-tests] dotnet not installed — skipping\n' >&2; exit 0; }

  start="$(cd "$(dirname "$FILE")" && pwd)"
  proj="$(find_up "$start" '*.csproj')" || {
    printf '[run-affected-tests] no .csproj above %s — skipping\n' "${FILE##*/}" >&2; exit 0; }

  target="$proj"
  case "${proj##*/}" in
    *Tests.csproj|*Test.csproj) : ;;                # edited a test project: run it
    *)
      # Edited a source project: run the fast unit-test project instead.
      # Integration and E2E suites are deliberately left to /qa — this hook has
      # to stay quick enough that nobody is tempted to switch it off.
      #
      # Walk up to the solution root (the directory holding the .sln/.slnx, or
      # failing that one holding a tests/ directory) before searching, because
      # src/ and tests/ are siblings.
      slndir=""
      d="$(cd "$(dirname "$proj")" && pwd -P)"
      while [ -n "$d" ] && [ "$d" != "/" ]; do
        if [ -n "$(find "$d" -maxdepth 1 \( -name '*.sln' -o -name '*.slnx' \) -print 2>/dev/null | head -n 1)" ] \
           || [ -d "$d/tests" ]; then
          slndir="$d"; break
        fi
        d="$(dirname "$d")"
      done
      [ -z "$slndir" ] && slndir="$(dirname "$(dirname "$proj")")"

      unit="$(find "$slndir" -maxdepth 4 -name '*UnitTests.csproj' -print 2>/dev/null | head -n 1)"
      if [ -z "$unit" ]; then
        printf '[run-affected-tests] no unit-test project found near %s — skipping\n' \
          "${proj##*/}" >&2
        exit 0
      fi
      target="$unit"
      ;;
  esac

  out="$(run_bounded dotnet test "$target" --nologo --no-restore \
          --verbosity quiet 2>&1)"
  rc=$?

  if [ "$rc" -eq 0 ]; then
    exit 0                                  # quiet on success
  fi
  if [ "$rc" -eq 124 ]; then
    printf '[run-affected-tests] %s exceeded %ss — not run to completion. Run it yourself.\n' \
      "${target##*/}" "$TIMEOUT_S" >&2
    exit 1
  fi

  {
    printf '[run-affected-tests] TESTS FAILING in %s\n\n' "${target##*/}"
    printf '%s\n' "$out" | tail -n 60
    printf '\nFix these before continuing — do not stack more changes on a red suite.\n'
  } >&2
  exit 1                                    # non-blocking: surfaced, not enforced
}

# ---------------------------------------------------------------------------
# Frontend
# ---------------------------------------------------------------------------
run_nx() {
  command -v npx >/dev/null 2>&1 || {
    printf '[run-affected-tests] npx not installed — skipping\n' >&2; exit 0; }

  start="$(cd "$(dirname "$FILE")" && pwd)"
  pj="$(find_up "$start" 'project.json')" || {
    printf '[run-affected-tests] no Nx project.json above %s — skipping\n' \
      "${FILE##*/}" >&2; exit 0; }

  name=""
  if command -v jq >/dev/null 2>&1; then
    name="$(jq -r '.name // empty' "$pj" 2>/dev/null)"
  elif command -v python3 >/dev/null 2>&1; then
    name="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("name",""))' \
             "$pj" 2>/dev/null)"
  fi
  [ -z "$name" ] && name="$(sed -n 's/.*"name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$pj" | head -n 1)"
  [ -z "$name" ] && { printf '[run-affected-tests] could not read the Nx project name — skipping\n' >&2; exit 0; }

  wsroot="$(dirname "$pj")"
  while [ "$wsroot" != "/" ] && [ ! -f "$wsroot/nx.json" ]; do
    wsroot="$(dirname "$wsroot")"
  done
  [ -f "$wsroot/nx.json" ] || { printf '[run-affected-tests] no nx.json found — skipping\n' >&2; exit 0; }

  out="$(cd "$wsroot" && run_bounded npx --no-install nx test "$name" --skip-nx-cache=false 2>&1)"
  rc=$?

  if [ "$rc" -eq 0 ]; then exit 0; fi
  if [ "$rc" -eq 127 ]; then
    printf '[run-affected-tests] nx is not installed in this workspace — skipping\n' >&2
    exit 0
  fi
  if [ "$rc" -eq 124 ]; then
    printf '[run-affected-tests] nx test %s exceeded %ss — not run to completion.\n' \
      "$name" "$TIMEOUT_S" >&2
    exit 1
  fi

  {
    printf '[run-affected-tests] TESTS FAILING in Nx project "%s"\n\n' "$name"
    printf '%s\n' "$out" | tail -n 60
  } >&2
  exit 1
}

case "$FILE" in
  *.cs)                       run_dotnet ;;
  *.ts|*.tsx|*.mts|*.cts)     run_nx ;;
  *)                          exit 0 ;;
esac

exit 0
