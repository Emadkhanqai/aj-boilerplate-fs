#!/usr/bin/env bash
# secret-scan.sh — PostToolUse(Edit|Write), and usable as a git pre-commit hook
#
# Two modes:
#   1. Hook mode   — a hook JSON payload arrives on stdin; scans the edited file.
#   2. Commit mode — no payload (or --staged); scans every staged file.
#      Install with:  ln -s ../../.claude/hooks/secret-scan.sh .git/hooks/pre-commit
#
# Uses gitleaks when it is installed; otherwise falls back to a built-in regex
# set covering API keys, PEM blocks, connection strings with passwords, JWTs,
# and cloud credentials.
#
# Exit 2 on a hit. Findings are printed with the value REDACTED — the point is to
# tell you where it is, not to print it again.

set -u

PAYLOAD=""
MODE="hook"
if [ "${1:-}" = "--staged" ] || [ -n "${GIT_INDEX_FILE:-}" ]; then
  MODE="commit"
else
  PAYLOAD="$(cat 2>/dev/null || true)"
  [ -z "$PAYLOAD" ] && MODE="commit"
fi

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

# --- Which files are we scanning? -------------------------------------------
FILES=""
if [ "$MODE" = "hook" ]; then
  f="$(json_str 'tool_input.file_path')"
  [ -n "$f" ] && [ -f "$f" ] && FILES="$f"
else
  if command -v git >/dev/null 2>&1 && git rev-parse --git-dir >/dev/null 2>&1; then
    FILES="$(git diff --cached --name-only --diff-filter=ACM 2>/dev/null)"
  fi
fi
[ -z "$FILES" ] && exit 0

FOUND=0
REPORT=""

add_finding() {   # add_finding <file> <line-no> <what> <line-text>
  FOUND=1
  _redacted="$(printf '%s' "$4" | cut -c1-40 | sed 's/[A-Za-z0-9+\/_=-]\{12,\}/[REDACTED]/g')"
  REPORT="${REPORT}
  $1:$2
    $3
    > ${_redacted}"
}

# --- gitleaks, when available -----------------------------------------------
# Gitleaks runs FIRST when installed, and the built-in patterns run regardless.
# They are complementary, not alternatives: gitleaks has far broader provider
# coverage, but its default ruleset does not flag a connection string carrying a
# password, which is exactly the leak this codebase is most likely to produce.
if command -v gitleaks >/dev/null 2>&1; then
  for f in $FILES; do
    [ -f "$f" ] || continue
    if ! gitleaks detect --no-banner --redact --no-git --source "$f" \
           --exit-code 1 >"/tmp/.gitleaks.$$" 2>&1; then
      FOUND=1
      REPORT="${REPORT}
  $f  (gitleaks)
$(sed 's/^/    /' "/tmp/.gitleaks.$$" | head -n 20)"
    fi
    rm -f "/tmp/.gitleaks.$$"
  done
else
  printf '[secret-scan] gitleaks not installed — built-in pattern set only\n' >&2
fi

# --- Built-in patterns (always run) -----------------------------------------

# A line is ignored when it is obviously a placeholder rather than a real value.
is_placeholder() {
  printf '%s' "$1" | grep -Eqi \
    '\$\{|\$\(|<your|your[-_]|example\.com|changeme|change-me|placeholder|REPLACE_ME|TODO|xxxxx|\*\*\*\*|dummy|sample|fake|<[A-Za-z_]+>|\.\.\.'
}

SELF="$(cd "$(dirname "$0")" 2>/dev/null && pwd -P)/$(basename "$0")"

scan_file() {
  f="$1"
  [ -f "$f" ] || return 0
  case "$f" in
    */node_modules/*|*/bin/*|*/obj/*|*/dist/*|*.lock|*package-lock.json|*.min.js) return 0 ;;
  esac
  # This file necessarily contains every pattern it looks for. Scanning it would
  # report a permanent, meaningless finding on every run.
  _abs="$(cd "$(dirname "$f")" 2>/dev/null && pwd -P)/$(basename "$f")"
  [ "$_abs" = "$SELF" ] && return 0
  # Skip binaries.
  if command -v file >/dev/null 2>&1; then
    case "$(file -b --mime-encoding "$f" 2>/dev/null)" in binary) return 0 ;; esac
  fi

  n=0
  while IFS= read -r line || [ -n "$line" ]; do
    n=$((n + 1))
    [ ${#line} -gt 2000 ] && continue
    is_placeholder "$line" && continue

    case_hit=""
    printf '%s' "$line" | grep -Eq -- '-----BEGIN [A-Z ]*PRIVATE KEY-----' \
      && case_hit="PEM private key block"
    [ -z "$case_hit" ] && printf '%s' "$line" | grep -Eq -- '(AKIA|ASIA)[0-9A-Z]{16}' \
      && case_hit="AWS access key id"
    [ -z "$case_hit" ] && printf '%s' "$line" | grep -Eq -- 'aws_secret_access_key[[:space:]]*=[[:space:]]*[A-Za-z0-9/+=]{30,}' \
      && case_hit="AWS secret access key"
    [ -z "$case_hit" ] && printf '%s' "$line" | grep -Eq -- 'AIza[0-9A-Za-z_-]{30,}' \
      && case_hit="Google API key"
    [ -z "$case_hit" ] && printf '%s' "$line" | grep -Eq -- '"private_key_id"|"type"[[:space:]]*:[[:space:]]*"service_account"' \
      && case_hit="Google service-account key material"
    [ -z "$case_hit" ] && printf '%s' "$line" | grep -Eq -- 'AccountKey=[A-Za-z0-9+/=]{40,}' \
      && case_hit="Azure storage account key"
    [ -z "$case_hit" ] && printf '%s' "$line" | grep -Eq -- '(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}' \
      && case_hit="GitHub token"
    [ -z "$case_hit" ] && printf '%s' "$line" | grep -Eq -- 'xox[baprs]-[A-Za-z0-9-]{10,}' \
      && case_hit="Slack token"
    [ -z "$case_hit" ] && printf '%s' "$line" | grep -Eq -- 'sq[apu]_[a-f0-9]{40}' \
      && case_hit="SonarQube token"
    [ -z "$case_hit" ] && printf '%s' "$line" | grep -Eq -- 'eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}' \
      && case_hit="JWT (signed token)"
    [ -z "$case_hit" ] && printf '%s' "$line" | grep -Eqi -- '(Server|Data Source|Host)[[:space:]]*=[^;]+;.*(Password|Pwd)[[:space:]]*=[^;[:space:]]{4,}' \
      && case_hit="connection string containing a password"
    [ -z "$case_hit" ] && printf '%s' "$line" | grep -Eqi -- '(api[_-]?key|apikey|secret|client[_-]?secret|access[_-]?token|auth[_-]?token|password)[[:space:]]*[:=][[:space:]]*["'"'"'][A-Za-z0-9_\-\.\/+=]{16,}["'"'"']' \
      && case_hit="hard-coded credential"
    [ -z "$case_hit" ] && printf '%s' "$line" | grep -Eq -- '-----BEGIN OPENSSH PRIVATE KEY-----' \
      && case_hit="SSH private key"

    [ -n "$case_hit" ] && add_finding "$f" "$n" "$case_hit" "$line"
  done < "$f"
}

for f in $FILES; do scan_file "$f"; done

if [ "$FOUND" -eq 1 ]; then
  cat >&2 <<EOF
BLOCKED by .claude/hooks/secret-scan.sh — possible secret detected
$REPORT

Treat any value that reached a file as COMPROMISED:
  1. Rotate it at the source.
  2. Remove it from the file (and from git history if it was ever committed).
  3. Reference it from the secret store instead — see .claude/standards/cloud.md.

If this is genuinely a placeholder, make it look like one (\${VAR}, <your-value>,
example.com) so the scanner and the next reader both understand it.
EOF
  exit 2
fi

exit 0
