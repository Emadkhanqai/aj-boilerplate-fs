#!/usr/bin/env bash
# sonar-pre-push.sh — PreToolUse(Bash), acts only on `git push`
#
# Enforces the SonarQube quality gate before anything leaves the machine.
# Exit 2 (block) while ANY Blocker/Critical/Major issue is open, printing the
# offending issues so the agent can fix them and retry.
#
# FAILS CLOSED. If SonarQube is unreachable or unconfigured, the gate has NOT
# passed and the push is blocked, with an explanation of how to configure it.
#
# TARGETS SONARQUBE COMMUNITY BUILD (the free, self-hosted edition). Community
# analyses exactly ONE branch — the project's main/default branch — so this hook
# queries the default branch and passes neither `branch` nor `pullRequest`.
# Branch analysis and pull-request analysis/decoration start at Developer
# Edition; this boilerplate deliberately does not depend on either.
#
# Configuration (environment, never committed):
#   SONAR_HOST_URL     e.g. http://localhost:9000
#   SONAR_TOKEN        analysis/user token — a secret
#   SONAR_PROJECT_KEY  falls back to sonar-project.properties
#   SONAR_RUN_SCAN=1   run a full scan before querying (slow; off by default)
#
#   SONAR_BRANCH       OPT-IN, OFF BY DEFAULT, AND PAID-EDITION ONLY.
#                      Appends `&branch=…` to the API calls. That parameter needs
#                      Developer Edition or above; on Community Build the server
#                      has no such branch to report on and the gate will fail
#                      closed. Leave it unset unless you are on a paid edition.
#
# ESCAPE HATCH:
#   SONAR_GATE_SKIP=1  bypasses this gate.
#   It exists for first-run bootstrap, before a SonarQube project exists.
#   USING IT IS A TECH-LEAD DECISION, NOT A DEVELOPER CONVENIENCE. Code pushed
#   under the skip has not been analysed and must be scanned before merge.

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
    | sed -n "s/.*\"${_leaf}\"[[:space:]]*:[[:space:]]*\"\(.*\)\".*/\1/p" \
    | head -n 1
}

CMD="$(json_str 'tool_input.command')"
[ -z "${CMD:-}" ] && exit 0

# Only interested in an actual push.
printf '%s' "$CMD" | grep -Eq '(^|[;&|(]|\s)git\s+([^;&|]*\s)?push(\s|$)' || exit 0
# `--dry-run` publishes nothing.
printf '%s' "$CMD" | grep -Eq '\-\-dry-run' && exit 0

block() {
  printf 'BLOCKED by .claude/hooks/sonar-pre-push.sh\n\n%s\n' "$1" >&2
  cat >&2 <<'EOF'

The SonarQube gate must pass before any push. See .claude/standards/sonarqube.md.

Bootstrap escape hatch (tech-lead decision only):
    SONAR_GATE_SKIP=1 git push ...
Code pushed under the skip has NOT been analysed and must be scanned before merge.
EOF
  exit 2
}

# --- Escape hatch -----------------------------------------------------------
if [ "${SONAR_GATE_SKIP:-0}" = "1" ]; then
  cat >&2 <<'EOF'
=============================================================================
 SONAR_GATE_SKIP=1 — THE QUALITY GATE WAS BYPASSED
 This push has NOT been analysed by SonarQube.
 This is a tech-lead decision. Record why in the pull request, and scan the
 branch before it is merged.
=============================================================================
EOF
  exit 0
fi

command -v curl >/dev/null 2>&1 || \
  block "curl is not installed, so the gate cannot be queried. The gate has NOT passed."

# --- Resolve configuration --------------------------------------------------
HOST="${SONAR_HOST_URL:-}"
TOKEN="${SONAR_TOKEN:-}"
KEY="${SONAR_PROJECT_KEY:-}"

if [ -z "$KEY" ]; then
  root="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
  for cand in "$root/sonar-project.properties" "$root/src/backend/sonar-project.properties"; do
    if [ -f "$cand" ]; then
      KEY="$(sed -n 's/^[[:space:]]*sonar\.projectKey[[:space:]]*=[[:space:]]*//p' "$cand" | head -n 1)"
      [ -n "$KEY" ] && break
    fi
  done
fi
if [ -z "$KEY" ] && [ -f "$(git rev-parse --show-toplevel 2>/dev/null)/.sonarlint/connectedMode.json" ]; then
  KEY="$(sed -n 's/.*"projectKey"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
    "$(git rev-parse --show-toplevel)/.sonarlint/connectedMode.json" | head -n 1)"
fi

[ -z "$HOST" ]  && block "SONAR_HOST_URL is not set. The gate could not run, so it has NOT passed."
[ -z "$TOKEN" ] && block "SONAR_TOKEN is not set. The gate could not run, so it has NOT passed."
[ -z "$KEY" ]   && block "The SonarQube project key could not be resolved.
Set SONAR_PROJECT_KEY, or add sonar.projectKey to sonar-project.properties."

HOST="${HOST%/}"

# --- Branch scope -----------------------------------------------------------
# Default (Community Build): no `branch` parameter at all. Community analyses a
# single branch, so the unqualified query IS the main-branch query — which is
# also what the SonarQube MCP server does when you omit `branch`/`pullRequest`.
# There is no `pullRequest` support here by design: PR analysis is a paid
# feature, and a gate that silently no-ops on Community would be worse than none.
BRANCH_Q=""
if [ -n "${SONAR_BRANCH:-}" ]; then
  BRANCH_Q="&branch=${SONAR_BRANCH}"
  printf '[sonar-pre-push] SONAR_BRANCH=%s — querying a named branch.\n' "${SONAR_BRANCH}" >&2
  printf '[sonar-pre-push] This requires SonarQube Developer Edition or above. On Community\n' >&2
  printf '[sonar-pre-push] Build, unset SONAR_BRANCH: the default branch is the only one there is.\n' >&2
fi

api() {   # api <path-with-query>  -> body on stdout, non-zero on transport failure
  # Bearer rather than `-u user:pass`: same result, and it does not trip the
  # secret scanners' curl-credential rule on every single run. A scanner that
  # cries wolf about its own tooling is a scanner people learn to ignore.
  curl -sS --fail-with-body --max-time 30 \
    -H "Authorization: Bearer ${TOKEN}" "${HOST}$1" 2>&1
}

# --- Optionally run the scan first ------------------------------------------
if [ "${SONAR_RUN_SCAN:-0}" = "1" ]; then
  if command -v dotnet >/dev/null 2>&1; then
    printf '[sonar-pre-push] running the scanner (SONAR_RUN_SCAN=1) — this takes a while...\n' >&2
    root="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
    ( cd "$root/src/backend" 2>/dev/null || cd "$root"
      dotnet sonarscanner begin /k:"$KEY" /d:sonar.host.url="$HOST" /d:sonar.token="$TOKEN" \
        && dotnet build --no-incremental \
        && dotnet sonarscanner end /d:sonar.token="$TOKEN" ) >&2 \
      || block "The SonarQube scan itself failed. Fix the scan before pushing."
  else
    block "SONAR_RUN_SCAN=1 but dotnet is not installed, so no scan could be produced."
  fi
fi

# --- Query the quality gate -------------------------------------------------
GATE="$(api "/api/qualitygates/project_status?projectKey=${KEY}${BRANCH_Q}")" || \
  block "Could not reach SonarQube at ${HOST}.
The gate could not be evaluated, so it has NOT passed.
Start the server, check SONAR_HOST_URL / SONAR_TOKEN, then retry.

  ${GATE}"

printf '%s' "$GATE" | grep -q '"projectStatus"' || \
  block "SonarQube returned an unexpected response for project '${KEY}'.
The gate has NOT passed.

If SONAR_BRANCH is set, unset it: naming a branch needs Developer Edition or
above, and SonarQube Community Build rejects it.

  $(printf '%s' "$GATE" | head -c 400)"

STATUS="$(printf '%s' "$GATE" \
  | sed -n 's/.*"projectStatus"[[:space:]]*:[[:space:]]*{[[:space:]]*"status"[[:space:]]*:[[:space:]]*"\([A-Z]*\)".*/\1/p' \
  | head -n 1)"
[ -z "$STATUS" ] && STATUS="$(printf '%s' "$GATE" | sed -n 's/.*"status"[[:space:]]*:[[:space:]]*"\([A-Z]*\)".*/\1/p' | head -n 1)"

# --- Query open Blocker/Critical/Major issues -------------------------------
ISSUES="$(api "/api/issues/search?componentKeys=${KEY}${BRANCH_Q}&severities=BLOCKER,CRITICAL,MAJOR&resolved=false&ps=25")" || \
  block "Could not read the issue list from SonarQube. The gate has NOT passed.

  ${ISSUES}"

TOTAL="$(printf '%s' "$ISSUES" | sed -n 's/.*"total"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p' | head -n 1)"
[ -z "$TOTAL" ] && TOTAL=0

if [ "$TOTAL" -gt 0 ] 2>/dev/null; then
  {
    printf 'BLOCKED by .claude/hooks/sonar-pre-push.sh\n\n'
    printf '%s open Blocker/Critical/Major issue(s) on project "%s".\n' "$TOTAL" "$KEY"
    printf 'Every one of them must be fixed before this push.\n\n'
    if command -v jq >/dev/null 2>&1; then
      printf '%s' "$ISSUES" | jq -r \
        '.issues[] | "  [\(.severity)] \(.component | split(":") | last):\(.line // 0)\n      \(.message)\n      rule: \(.rule)"' \
        2>/dev/null | head -n 90
    elif command -v python3 >/dev/null 2>&1; then
      printf '%s' "$ISSUES" | python3 -c '
import sys, json
try:
    d = json.load(sys.stdin)
except Exception:
    sys.exit(0)
for i in d.get("issues", [])[:25]:
    comp = i.get("component", "").split(":")[-1]
    print("  [%s] %s:%s" % (i.get("severity"), comp, i.get("line", 0)))
    print("      %s" % i.get("message", ""))
    print("      rule: %s" % i.get("rule", ""))
' 2>/dev/null
    else
      printf '%s\n' "$ISSUES" | head -c 2000
    fi
    printf '\nFix them, rescan, and retry. Do not suppress an issue to pass the gate\n'
    printf 'without explicit approval and a recorded reason.\n'
    printf 'See .claude/standards/sonarqube.md\n'
  } >&2
  exit 2
fi

if [ "$STATUS" != "OK" ]; then
  block "The SonarQube quality gate status for '${KEY}' is '${STATUS:-UNKNOWN}', not OK.
Open the project in SonarQube to see which condition failed (commonly coverage
or duplication on new code)."
fi

printf '[sonar-pre-push] gate OK for %s — 0 open Blocker/Critical/Major. Push allowed.\n' "$KEY" >&2
exit 0
