#!/usr/bin/env bash
# block-dangerous.sh — PreToolUse(Bash)
#
# Refuses irreversible or production-touching shell commands. Exit 2 blocks the
# tool call and shows the reason to the agent, which can then choose a safe
# alternative or ask the human to run it deliberately.
#
# There is NO bypass environment variable. These operations are human decisions
# made outside an agent session, on purpose.

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

block() {
  cat >&2 <<EOF
BLOCKED by .claude/hooks/block-dangerous.sh

  Command: $CMD
  Reason:  $1

  $2

This is a deliberate guardrail. Do not work around it — if this operation is
genuinely required, a human runs it outside the agent session, on purpose.
EOF
  exit 2
}

m() { printf '%s' "$CMD" | grep -Eqi -- "$1"; }

# --- Recursive force delete -------------------------------------------------
if m '(^|[;&|(]|\s)rm\s+(-[a-zA-Z]*\s+)*-[a-zA-Z]*[rR][a-zA-Z]*f|(^|[;&|(]|\s)rm\s+(-[a-zA-Z]*\s+)*-[a-zA-Z]*f[a-zA-Z]*[rR]'; then
  block "recursive force delete (rm -rf)" \
        "Delete specific paths explicitly, or move them aside instead of removing them."
fi

# --- History rewriting / destructive git ------------------------------------
if m 'git\s+push\b.*(--force-with-lease|--force|(\s|^)-f(\s|$))'; then
  block "force push rewrites published history" \
        "Land the change as a new commit. A force push is never an agent decision."
fi
if m 'git\s+reset\s+.*--hard'; then
  block "git reset --hard discards uncommitted work irrecoverably" \
        "Use 'git stash' to set changes aside, or 'git restore <path>' for one file."
fi
if m 'git\s+clean\b.*-[a-zA-Z]*[dfx]'; then
  block "git clean removes untracked files, including ones never committed" \
        "Remove the specific files you mean to remove."
fi
if m 'git\s+(branch\s+-D|update-ref\s+-d|reflog\s+expire|filter-branch|filter-repo)'; then
  block "destructive git history or ref operation" \
        "History surgery is a human decision made deliberately, outside an agent session."
fi

# --- Destructive SQL --------------------------------------------------------
if m 'DROP\s+(DATABASE|SCHEMA)\b'; then
  block "DROP DATABASE / DROP SCHEMA destroys data" \
        "Schema changes go through a reviewed EF Core migration — see /new-migration."
fi
if m 'TRUNCATE\s+(TABLE\s+)?[A-Za-z_\[]'; then
  block "TRUNCATE empties a table with no recoverable log" \
        "Use a scoped DELETE inside a transaction, or a reviewed migration."
fi
if m 'DROP\s+TABLE\b'; then
  block "DROP TABLE destroys data" \
        "Express the change as an EF Core migration so it is reviewed and reversible."
fi

# --- Production connection strings ------------------------------------------
if m '(Server|Data\s*Source|Host)\s*=[^;]*(prod|production)'; then
  block "connection string points at a production database" \
        "Agents work against local or development databases only."
fi
if m '(Password|Pwd)\s*=\s*[^;[:space:]]+' && m '(Server|Data\s*Source|Initial\s*Catalog)\s*='; then
  block "connection string contains an inline password" \
        "Use dotnet user-secrets locally and the provider secret store in the cloud."
fi

# --- Production cloud operations --------------------------------------------
if m '(^|[;&|(]|\s)gcloud\b' && m '(--project[= ][^ ]*(prod|prd)|--configuration[= ][^ ]*prod)'; then
  block "gcloud command targets a production project" \
        "Production changes go through the reviewed deployment pipeline."
fi
if m '(^|[;&|(]|\s)az\b' && m '(prod|production)'; then
  block "az command references a production resource" \
        "Production changes go through the reviewed deployment pipeline."
fi
if m '(^|[;&|(]|\s)(kubectl|helm)\b' && m '(prod|production)'; then
  block "cluster operation targets a production context or namespace" \
        "Production changes go through the reviewed deployment pipeline."
fi
if m '(^|[;&|(]|\s)terraform\s+(apply|destroy)' || m '(^|[;&|(]|\s)az\s+deployment\s+.*create'; then
  block "infrastructure apply/destroy from an agent session" \
        "Run 'terraform plan' / a what-if instead; applying is a pipeline step."
fi
if m 'gcloud\s+.*(sql\s+instances\s+delete|projects\s+delete)' \
   || m 'az\s+.*(group\s+delete|sql\s+db\s+delete)'; then
  block "cloud resource deletion" \
        "Deleting managed infrastructure is never an agent action."
fi

# --- Credential exfiltration shapes -----------------------------------------
if m 'curl\s+.*(SONAR_TOKEN|GITHUB_TOKEN|AWS_SECRET|API_KEY).*\s(-d|--data|-T|--upload-file)'; then
  block "command would send a credential to a remote host" \
        "Never transmit a secret from an agent session."
fi

exit 0
