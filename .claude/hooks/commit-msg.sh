#!/usr/bin/env bash
# commit-msg.sh — Conventional Commits enforcement
#
# Two modes, mirroring secret-scan.sh:
#   1. Git mode    — invoked by git as a `commit-msg` hook with the message file as $1.
#                    Install with:  ln -s ../../.claude/hooks/commit-msg.sh .git/hooks/commit-msg
#   2. Hook mode   — PreToolUse(Bash). A hook JSON payload arrives on stdin; the message is
#                    extracted from a `git commit -m …` command and checked BEFORE it runs.
#
# Both modes exist because neither covers the other. Git mode catches every commit made by
# anyone, including one typed by hand in an editor — but only on a machine where somebody
# remembered to install it. Hook mode needs no installation and catches the commits an agent
# makes, which is most of them here, but it cannot see a message composed in an editor.
#
# Exit 2 on a violation, matching the other blocking hooks in this directory. Git aborts the
# commit on any non-zero status, so 2 works in both modes and there is one convention rather
# than two.
#
# WHY BLOCK RATHER THAN WARN. The commit history is the input to the release process: the
# CHANGELOG is assembled from it and the version bump is derived from the types present
# (see CONTRIBUTING.md). A warning produces a history that is 90% conventional, which is
# worse than none — it looks parseable, so somebody writes a parser, and the parser is wrong
# about the other 10%.
#
# ESCAPE HATCH:
#   COMMIT_MSG_SKIP=1  bypasses this check.
#   It exists for a message you genuinely do not control — an automated merge commit, a
#   `git revert` default message, a rebase fixup landing through tooling. It is not for
#   "I will tidy it up later"; the history is append-only in practice and you will not.

set -u

# ---------------------------------------------------------------------------
# Escape hatch
# ---------------------------------------------------------------------------
if [ "${COMMIT_MSG_SKIP:-0}" = "1" ]; then
  printf '[commit-msg] COMMIT_MSG_SKIP=1 — the Conventional Commits check was bypassed.\n' >&2
  exit 0
fi

# ---------------------------------------------------------------------------
# The allowed types. Keep this list and the table in CONTRIBUTING.md identical.
# ---------------------------------------------------------------------------
TYPES='feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert'

MAX_HEADER=100   # hard limit — blocks
SOFT_HEADER=72   # advisory — prints a note, does not block

# ---------------------------------------------------------------------------
# Payload helpers — identical idiom to the other hooks in this directory, so the
# three of them fail the same way on a machine with neither jq nor python3.
# ---------------------------------------------------------------------------
PAYLOAD=""

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

# Pulls the message out of a `git commit …` command line. Returns empty when the command
# is not a message-carrying commit, which the caller treats as "nothing to check here".
extract_message_from_command() {
  if command -v python3 >/dev/null 2>&1; then
    printf '%s' "$1" | python3 -c '
import sys, shlex

raw = sys.stdin.read()

# A command may be a chain: `git add -A && git commit -m "..."`. Split on the shell
# operators and look at each segment on its own, so a message containing the word
# "commit" in another segment cannot be mistaken for the real one.
segments, current, i = [], "", 0
while i < len(raw):
    two = raw[i:i + 2]
    if two in ("&&", "||", ";;"):
        segments.append(current); current = ""; i += 2; continue
    if raw[i] in ";\n":
        segments.append(current); current = ""; i += 1; continue
    current += raw[i]; i += 1
segments.append(current)

for seg in segments:
    try:
        parts = shlex.split(seg)
    except ValueError:
        continue
    if not parts:
        continue
    # Tolerate `env FOO=bar git commit`, `sudo git commit`, and a leading path.
    while parts and ("=" in parts[0] or parts[0] in ("env", "sudo", "command", "nice")):
        parts.pop(0)
    if not parts or not parts[0].endswith("git"):
        continue
    if "commit" not in parts:
        continue

    # `--amend --no-edit` reuses an existing message that was already checked when it
    # was first written. Nothing new to validate.
    if "--no-edit" in parts:
        sys.exit(0)

    pieces, j = [], 0
    while j < len(parts):
        p = parts[j]
        if p in ("-m", "--message") and j + 1 < len(parts):
            pieces.append(parts[j + 1]); j += 2; continue
        if p.startswith("--message="):
            pieces.append(p[len("--message="):]); j += 1; continue
        if p.startswith("-m") and len(p) > 2 and not p.startswith("--"):
            pieces.append(p[2:]); j += 1; continue
        j += 1
    if pieces:
        # Repeated -m flags become separate paragraphs, exactly as git joins them.
        print("\n\n".join(pieces))
    sys.exit(0)
' 2>/dev/null
    return 0
  fi

  # No python3. A deliberately conservative fallback: it handles the single-quoted and
  # double-quoted `-m` forms and nothing else. When it finds nothing, the caller allows
  # the commit rather than blocking on a message it could not read — a hook that blocks
  # what it cannot parse teaches people to set COMMIT_MSG_SKIP permanently.
  printf '%s' "$1" \
    | sed -n -e 's/.*-m[[:space:]]*"\([^"]*\)".*/\1/p' -e "s/.*-m[[:space:]]*'\([^']*\)'.*/\1/p" \
    | head -n 1
}

# ---------------------------------------------------------------------------
# Work out which mode we are in and get the message
# ---------------------------------------------------------------------------
MESSAGE=""
SOURCE=""

if [ "$#" -ge 1 ] && [ -f "$1" ]; then
  # Git mode. $1 is .git/COMMIT_EDITMSG.
  SOURCE="git commit-msg hook"
  MESSAGE="$(cat "$1")"
else
  PAYLOAD="$(cat 2>/dev/null || true)"
  [ -z "$PAYLOAD" ] && exit 0

  CMD="$(json_str 'tool_input.command')"
  [ -z "${CMD:-}" ] && exit 0

  # Only interested in an actual commit.
  printf '%s' "$CMD" | grep -Eq '(^|[;&|(]|[[:space:]])git[[:space:]]+([^;&|]*[[:space:]])?commit([[:space:]]|$)' || exit 0

  SOURCE="PreToolUse(Bash)"
  MESSAGE="$(extract_message_from_command "$CMD")"

  # No -m: git will open an editor, and there is nothing to inspect yet. The git-mode
  # hook covers that path if it is installed.
  [ -z "${MESSAGE:-}" ] && exit 0
fi

# ---------------------------------------------------------------------------
# Strip comment lines and the scissors section, exactly as git does, then take the
# first non-empty line as the header.
# ---------------------------------------------------------------------------
CLEAN="$(printf '%s\n' "$MESSAGE" \
  | sed -e '/^# ------------------------ >8 ------------------------$/,$d' \
        -e '/^[[:space:]]*#/d')"

HEADER="$(printf '%s\n' "$CLEAN" | sed -e '/^[[:space:]]*$/d' | head -n 1)"

# An empty message aborts the commit on git's side anyway; say nothing and let it.
[ -z "${HEADER:-}" ] && exit 0

# ---------------------------------------------------------------------------
# Messages git itself generates, which we neither control nor rewrite.
# ---------------------------------------------------------------------------
case "$HEADER" in
  "Merge "*|"Revert "*|"fixup! "*|"squash! "*|"amend! "*|"Applying: "*)
    exit 0
    ;;
esac
# An initial commit before any convention exists.
printf '%s' "$HEADER" | grep -Eq '^Initial commit$' && exit 0

# ---------------------------------------------------------------------------
# The checks
# ---------------------------------------------------------------------------
PROBLEMS=""
add_problem() { PROBLEMS="${PROBLEMS}
  - $1"; }

# 1. Shape: type(optional scope)optional-!: space, then a subject.
if ! printf '%s' "$HEADER" | grep -Eq "^(${TYPES})(\([a-z0-9][a-z0-9._/-]*\))?!?: .+"; then
  if printf '%s' "$HEADER" | grep -Eq "^(${TYPES})(\([^)]*\))?!?:"; then
    # Right type, wrong punctuation or an empty subject.
    add_problem "the type is right, but what follows is not ': ' plus a subject (one colon, exactly one space, then text)"
  elif printf '%s' "$HEADER" | grep -Eq '^[a-zA-Z]+(\([^)]*\))?!?:'; then
    _type="$(printf '%s' "$HEADER" | sed -n 's/^\([a-zA-Z]*\).*/\1/p')"
    add_problem "'${_type}' is not one of the allowed types: ${TYPES//|/, }"
  else
    add_problem "no Conventional Commits prefix. The header must start with a type, e.g. 'fix: …'"
  fi
fi

# 2. Length. Over 100 blocks; over 72 is a note, because 72 is a rendering preference and
#    100 is the point at which git log, GitHub, and every changelog tool start truncating.
HEADER_LEN=${#HEADER}
if [ "$HEADER_LEN" -gt "$MAX_HEADER" ]; then
  add_problem "the header is ${HEADER_LEN} characters; the limit is ${MAX_HEADER}"
fi

# 3. A trailing full stop. The header is a title, not a sentence, and the stop is pure
#    noise in every tool that lists commits one per line.
printf '%s' "$HEADER" | grep -Eq '\.$' \
  && add_problem "the header ends with a full stop — remove it"

# 4. A subject that is only a type. `fix: bug` and `chore: stuff` say nothing.
printf '%s' "$HEADER" | grep -Eiq ": *(wip|stuff|things|fixes?|updates?|changes?|misc|minor)$" \
  && add_problem "the subject says nothing. Describe what changed and why, not that something changed"

# 5. A body must be separated from the header by a blank line. Git treats the first
#    paragraph as the subject, so without the blank line the whole thing becomes one
#    enormous header.
SECOND_LINE="$(printf '%s\n' "$CLEAN" | sed -n '2p')"
if [ -n "${SECOND_LINE:-}" ] && [ "$(printf '%s' "$SECOND_LINE" | tr -d '[:space:]')" != "" ]; then
  add_problem "line 2 is not blank. Leave one blank line between the header and the body"
fi

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------
if [ -n "$PROBLEMS" ]; then
  {
    printf 'BLOCKED by .claude/hooks/commit-msg.sh (%s) — the commit message is not a\n' "$SOURCE"
    printf 'Conventional Commit.\n\n'
    printf '  %s\n' "$HEADER"
    printf '\nProblems:%s\n' "$PROBLEMS"
    cat <<EOF

Format:

  <type>[optional scope][!]: <subject>

  [optional body]

  [optional footer(s)]

Types:  ${TYPES//|/, }

A '!' after the type or scope, or a 'BREAKING CHANGE:' footer, marks a breaking change
and drives a major version bump.

Examples:

  feat(api): return a stable error code on validation failure
  fix(web): stop the items grid refetching on every keystroke
  refactor(infrastructure)!: rename ISecretsProvider.GetAsync to FetchAsync
  docs: record the three-repository sync mechanism in ADR-0011
  ci: scan container images and publish an SBOM

Full convention: CONTRIBUTING.md · https://www.conventionalcommits.org/en/v1.0.0/

Escape hatch, for a message you genuinely do not control:
    COMMIT_MSG_SKIP=1 git commit ...
EOF
  } >&2
  exit 2
fi

if [ "$HEADER_LEN" -gt "$SOFT_HEADER" ]; then
  printf '[commit-msg] note: the header is %s characters. Under %s reads better in `git log --oneline` and on GitHub.\n' \
    "$HEADER_LEN" "$SOFT_HEADER" >&2
fi

exit 0
