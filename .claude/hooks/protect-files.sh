#!/usr/bin/env bash
# protect-files.sh — PreToolUse(Edit|Write)
#
# Blocks edits to files that must never be changed by an agent:
#   - .env and friends (real ones; .env.example / .template / .sample are fine)
#   - appsettings.Production.json and any production settings file
#   - EF Core migration files that ALREADY EXIST (i.e. have been generated and,
#     by assumption, applied somewhere) plus the model snapshot
#   - infra/*/prod/**
#   - .claude/settings.json itself
#   - key material (.pem, .key, service-account JSON)
#
# Exit 2 blocks the tool call and explains why.
#
# Note on migrations: a hook cannot query every environment's __EFMigrationsHistory,
# so it applies the safe rule — creating a NEW migration file is allowed (the path
# does not exist yet), editing an EXISTING one is not. Fix forward with a new
# migration instead.

set -u

PAYLOAD="$(cat 2>/dev/null || true)"

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
[ -z "${FILE:-}" ] && FILE="$(json_str 'tool_input.notebook_path')"
[ -z "${FILE:-}" ] && exit 0

BASE="${FILE##*/}"

block() {
  cat >&2 <<EOF
BLOCKED by .claude/hooks/protect-files.sh

  File:   $FILE
  Reason: $1

  $2
EOF
  exit 2
}

# --- Environment files ------------------------------------------------------
case "$BASE" in
  .env.example|.env.template|.env.sample|.env.dist) ;;   # templates are fine
  .env|.env.*|*.env)
    block "environment files hold real credentials" \
          "Edit .env.example instead and tell the developer which variable to set."
    ;;
esac

# --- Production application settings ----------------------------------------
case "$BASE" in
  appsettings.Production.json|appsettings.Prod.json|appsettings.Staging.json)
    block "production/staging application settings are deployment-managed" \
          "Change appsettings.json or appsettings.Development.json; deployed values come from the pipeline and the secret store."
    ;;
esac
case "$FILE" in
  *appsettings.*Production*.json|*appsettings.*Prod*.json)
    block "production application settings are deployment-managed" \
          "Change appsettings.json or appsettings.Development.json instead."
    ;;
esac

# --- Existing EF Core migrations --------------------------------------------
case "$FILE" in
  */Migrations/*)
    if [ -e "$FILE" ]; then
      block "this EF Core migration already exists and may have been applied" \
            "Never edit an applied migration — create a new one with /new-migration and fix forward."
    fi
    ;;
esac
case "$BASE" in
  *ModelSnapshot.cs)
    if [ -e "$FILE" ]; then
      block "the EF Core model snapshot is generated, not hand-edited" \
            "Run 'dotnet ef migrations add <Name>' and let EF Core regenerate it."
    fi
    ;;
esac

# --- Production infrastructure ----------------------------------------------
case "$FILE" in
  */infra/*/prod/*|infra/*/prod/*|*/infra/prod/*|infra/prod/*)
    block "production infrastructure definitions are pipeline-managed" \
          "Change the non-prod environment first and promote it through the reviewed pipeline."
    ;;
esac

# --- The harness's own configuration ----------------------------------------
case "$FILE" in
  */.claude/settings.json|.claude/settings.json)
    block "settings.json wires the guardrails, including this hook" \
          "Changing it is a human decision. Use .claude/settings.local.json for personal overrides."
    ;;
esac

# --- Key material -----------------------------------------------------------
case "$BASE" in
  *.pem|*.p12|*.pfx|*.key|id_rsa|id_ed25519|*-service-account*.json|credentials.json)
    block "this looks like key material" \
          "Key material never lives in the repository. Reference it from the secret store."
    ;;
esac

exit 0
