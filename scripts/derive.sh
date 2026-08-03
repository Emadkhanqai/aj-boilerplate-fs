#!/usr/bin/env bash
# derive.sh — regenerate the single-stack repositories from this full-stack tree.
#
# ADR-0006 publishes this boilerplate as three repositories:
#
#   aj-boilerplate-fs   this tree — the source of truth
#   aj-boilerplate-be   src/backend/  promoted to the repository root
#   aj-boilerplate-fe   src/frontend/ promoted to the repository root
#
# It also lists "a derivation script" as follow-on work, and says plainly that drift is
# the standing risk of the decision. This is that script. ADR-0011 records why it exists
# and what it deliberately does not do.
#
# USAGE
#   scripts/derive.sh                       dry run — prints the plan, writes nothing
#   scripts/derive.sh --write               produce both derived trees
#   scripts/derive.sh --write --only be     just the backend repository
#   scripts/derive.sh --write --clean       overwrite a previous derivation (guarded)
#   scripts/derive.sh --write --out DIR     write somewhere other than dist/derive
#   scripts/derive.sh --check               verify an existing derivation, write nothing
#
# DRY RUN IS THE DEFAULT, and that is not politeness. This script's job is to write a
# large number of files to a path assembled from arguments, which is exactly the shape of
# operation that destroys someone's work when a variable is empty. You have to ask.
#
# SAFETY RULES, IN ORDER OF HOW MUCH THEY MATTER
#   1. Nothing is deleted without `--clean`, and `--clean` refuses to touch a directory
#      that this script did not create. It checks for the marker file it writes itself
#      (.derived-by-derive-sh) and refuses on anything else, including an empty-looking
#      directory. A path is not "obviously safe" because it looks like the one you meant.
#   2. The output directory must be an ABSOLUTE path, must be under the resolved output
#      base, and must have one of two known basenames. All three are checked, every time.
#   3. It never writes inside the source tree except under the output directory, which
#      defaults to `dist/`, which .gitignore already excludes.
#   4. It copies only files git is tracking. Build output, node_modules, .env files, and
#      anything else untracked cannot leak into a published repository, because the file
#      list comes from `git ls-files` rather than from the filesystem.
#
# WHAT IT DOES NOT DO: it does not create git repositories, does not commit, and does not
# push. It produces two directories. What happens to them is a human decision.

set -euo pipefail

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
MARKER='.derived-by-derive-sh'

# Paths that exist in this tree but must never reach a derived repository.
# One entry per line, matched as a prefix against the repository-relative path.
COMMON_DROP='
docker-compose.yml
'

BE_DROP='
src/frontend/
.github/workflows/frontend-ci.yml
'

# `sonar-project.properties` is dropped for fe and NOT for be, which looks asymmetric until you
# follow the promotion. This tree has two files by that name: the shared one at the root, and the
# frontend scanner's own at src/frontend/. In the fe repository src/frontend/ BECOMES the root, so
# both would claim the same path — and the promoted one has to win, because it is what the CLI
# scanner actually reads from projectBaseDir. Dropping the root copy here is what resolves that
# collision. The be repository has no such clash: SonarScanner for .NET reads the XML instead.
FE_DROP='
src/backend/
.github/workflows/backend-ci.yml
SonarQube.Analysis.xml
sonar-project.properties
'

# Files copied verbatim, WITHOUT the path rewriting described below, because rewriting
# them mechanically produces something plausible and wrong. Each one is reported at the
# end as needing a human.
REVIEW_REQUIRED='
.github/workflows/supply-chain.yml
.github/dependabot.yml
README.md
'

# ---------------------------------------------------------------------------
# Argument parsing
# ---------------------------------------------------------------------------
WRITE=0
CLEAN=0
CHECK=0
INCLUDE_UNTRACKED=0
ONLY=''
OUT_BASE=''

die() { printf 'derive.sh: %s\n' "$*" >&2; exit 1; }
note() { printf '  %s\n' "$*"; }

usage() {
  sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --write)  WRITE=1 ;;
    --clean)  CLEAN=1 ;;
    --check)  CHECK=1 ;;
    --include-untracked) INCLUDE_UNTRACKED=1 ;;
    --only)   shift; ONLY="${1:-}"; [ -n "$ONLY" ] || die '--only needs a value: be or fe' ;;
    --out)    shift; OUT_BASE="${1:-}"; [ -n "$OUT_BASE" ] || die '--out needs a directory' ;;
    -h|--help) usage 0 ;;
    *) die "unknown argument '$1' (try --help)" ;;
  esac
  shift
done

case "$ONLY" in
  ''|be|fe) ;;
  *) die "--only must be 'be' or 'fe', not '$ONLY'" ;;
esac

# ---------------------------------------------------------------------------
# Locate the source tree. Everything else is derived from this one value, so it is
# validated rather than assumed: a wrong root here is a wrong path in every operation
# below.
# ---------------------------------------------------------------------------
command -v git >/dev/null 2>&1 || die 'git is required.'

ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" \
  || die 'not inside a git repository.'

[ -d "$ROOT/src/backend" ]  || die "no src/backend under '$ROOT' — this is not the full-stack tree."
[ -d "$ROOT/src/frontend" ] || die "no src/frontend under '$ROOT' — this is not the full-stack tree."
[ -d "$ROOT/.claude" ]      || die "no .claude under '$ROOT' — this is not the full-stack tree."

if [ -z "$OUT_BASE" ]; then
  OUT_BASE="$ROOT/dist/derive"
fi
# Absolute, always. A relative output path resolved against whatever directory the caller
# happened to be in is how a script writes into somewhere surprising.
case "$OUT_BASE" in
  /*) ;;
  *)  OUT_BASE="$PWD/$OUT_BASE" ;;
esac

SOURCE_SHA="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo 'unknown')"
SOURCE_DIRTY=''
if ! git -C "$ROOT" diff --quiet HEAD 2>/dev/null; then
  SOURCE_DIRTY=' (working tree has uncommitted changes)'
fi

# Files git would track but that are not committed. Without --include-untracked they are
# silently absent from the derived trees, and "silently absent" is how a derivation ships
# a repository missing the file you just wrote.
UNTRACKED_COUNT="$(git -C "$ROOT" ls-files --others --exclude-standard | wc -l | tr -d ' ')" 

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

# matches_any <path> <newline-separated prefixes>  -> 0 when the path starts with one
matches_any() {
  _p="$1"; _list="$2"
  while IFS= read -r _prefix; do
    [ -z "$_prefix" ] && continue
    case "$_p" in
      "$_prefix"*) return 0 ;;
    esac
  done <<EOF
$_list
EOF
  return 1
}

# Rewrites in-file references to the promoted tree. `src/backend/foo` becomes `foo` once
# src/backend IS the root.
#
# THIS IS A HEURISTIC AND IT IS THE WEAKEST PART OF THE SCRIPT. It is a textual
# substitution over documentation and configuration; it cannot know that `'src/backend/**'`
# in a workflow path filter should become `'**'` rather than the empty string, and it does
# not try. Files where that matters are listed in REVIEW_REQUIRED and are copied untouched.
rewrite_paths() {
  _file="$1"; _promoted="$2"
  case "$_file" in
    *.md|*.yml|*.yaml|*.json|*.sh|*.properties|*.txt|*.xml|*.conf) ;;
    *) return 0 ;;
  esac
  # `src/backend/x` -> `x`, and a bare `src/backend` -> `.`
  sed -i.bak \
    -e "s#${_promoted}/#\./#g" \
    -e "s#\./\./#./#g" \
    -e "s#${_promoted}#.#g" \
    "$_file" && rm -f "$_file.bak"
}

# suffixed_name <relative-path> <target>  -> a non-colliding name in the same directory.
#
# Dotfiles are the awkward case and the common one: naive `${name%.*}` / `${name##*.}` on
# `.editorconfig` yields an empty base and produces `.be.editorconfig`, which is a
# different file that no tool reads. A leading dot with no further dot means the whole
# name is the base.
suffixed_name() {
  _rel="$1"; _suffix="$2"
  _dir="$(dirname "$_rel")"
  _name="$(basename "$_rel")"

  case "$_name" in
    .*)
      # `.editorconfig` -> `.editorconfig.be`; `.eslintrc.json` -> `.eslintrc.be.json`
      _stripped="${_name#.}"
      case "$_stripped" in
        *.*) _out=".${_stripped%.*}.${_suffix}.${_stripped##*.}" ;;
        *)   _out="${_name}.${_suffix}" ;;
      esac
      ;;
    *.*) _out="${_name%.*}.${_suffix}.${_name##*.}" ;;
    *)   _out="${_name}.${_suffix}" ;;
  esac

  if [ "$_dir" = '.' ]; then printf '%s' "$_out"; else printf '%s/%s' "$_dir" "$_out"; fi
}

# Guarded removal. Every condition below has to hold. This is the only place in the script
# that deletes anything, and it deletes only a directory this script created.
safe_remove_derived() {
  _dir="$1"

  case "$_dir" in
    /*) ;;
    *)  die "refusing to remove a relative path: '$_dir'" ;;
  esac
  case "$_dir" in
    "$OUT_BASE"/*) ;;
    *) die "refusing to remove '$_dir': it is not under the output base '$OUT_BASE'" ;;
  esac
  case "$(basename "$_dir")" in
    aj-boilerplate-be|aj-boilerplate-fe) ;;
    *) die "refusing to remove '$_dir': unexpected directory name" ;;
  esac
  [ -d "$_dir" ] || return 0
  if [ ! -f "$_dir/$MARKER" ]; then
    die "refusing to remove '$_dir': it has no $MARKER file, so this script did not create it.
Remove it yourself if you are sure, or choose a different --out."
  fi
  # Every condition above has held: absolute, inside the output base we constructed, named
  # by us, and carrying the marker file we wrote.
  rm -rf -- "$_dir"
}

# ---------------------------------------------------------------------------
# derive <target>   target = be | fe
# ---------------------------------------------------------------------------
derive() {
  target="$1"

  case "$target" in
    be) promoted='src/backend';  drop_list="$BE_DROP"; repo='aj-boilerplate-be'; label='Backend' ;;
    fe) promoted='src/frontend'; drop_list="$FE_DROP"; repo='aj-boilerplate-fe'; label='Frontend' ;;
    *)  die "unknown target '$target'" ;;
  esac

  dest="$OUT_BASE/$repo"

  printf '\n=== %s  ->  %s ===\n' "$label" "$dest"

  n_shared=0; n_promoted=0; n_dropped=0; n_collide=0; n_review=0
  collisions=''; review_hits=''

  # The file list comes from git, not from the filesystem. Build output, node_modules,
  # .env files, and anything else untracked cannot leak into a published repository if
  # they were never in the list.
  #
  # Tracked-only by default: you derive from what is committed, because that is what
  # anyone else can reproduce. `--include-untracked` adds files git WOULD track (still
  # honouring .gitignore) and exists for iterating locally before committing.
  if [ "$INCLUDE_UNTRACKED" -eq 1 ]; then
    files="$(git -C "$ROOT" ls-files --cached --others --exclude-standard)"
  else
    files="$(git -C "$ROOT" ls-files)"
  fi

  if [ "$WRITE" -eq 1 ]; then
    [ "$CLEAN" -eq 1 ] && safe_remove_derived "$dest"
    if [ -d "$dest" ] && [ -n "$(ls -A "$dest" 2>/dev/null)" ]; then
      die "'$dest' already exists and is not empty.
Re-run with --clean to replace it (only works on a directory this script created),
or pass a different --out."
    fi
    mkdir -p "$dest"
    {
      printf 'Generated by scripts/derive.sh from aj-boilerplate-fs.\n'
      printf 'Source commit: %s%s\n' "$SOURCE_SHA" "$SOURCE_DIRTY"
      printf 'Generated at:  %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
      printf '\nDo not edit this tree. Change aj-boilerplate-fs and re-derive.\n'
    } > "$dest/$MARKER"
  fi

  while IFS= read -r f; do
    [ -z "$f" ] && continue

    # 1. Dropped outright — the other stack, and files that only make sense full-stack.
    if matches_any "$f" "$drop_list" || matches_any "$f" "$COMMON_DROP"; then
      n_dropped=$((n_dropped + 1))
      continue
    fi

    # 2. Promoted — src/<stack>/x becomes x at the root.
    case "$f" in
      "$promoted"/*)
        rel="${f#"$promoted"/}"
        kind='promoted'
        ;;
      *)
        rel="$f"
        kind='shared'
        ;;
    esac

    # 3. Collision. A promoted file can land on a shared file of the same name — most
    #    obviously README.md, CLAUDE.md, .editorconfig, .gitignore, .dockerignore. The
    #    shared file is NOT silently overwritten; both are kept, the promoted one under a
    #    suffixed name, and the collision is reported.
    #
    #    A root file that is itself being dropped is not a collision: the promoted file
    #    takes the root name cleanly. `docker-compose.yml` is the case that matters —
    #    the full-stack compose is dropped, and the stack's own compose becomes the one
    #    at the root, which is exactly what a single-stack repository wants.
    out_rel="$rel"
    if [ "$kind" = 'promoted' ] && [ -e "$ROOT/$rel" ] \
       && ! matches_any "$rel" "$COMMON_DROP" && ! matches_any "$rel" "$drop_list"; then
      n_collide=$((n_collide + 1))
      out_rel="$(suffixed_name "$rel" "$target")"
      collisions="${collisions}
      ${f}  ->  ${out_rel}   (root ${rel} kept as-is)"
    fi

    if matches_any "$out_rel" "$REVIEW_REQUIRED"; then
      n_review=$((n_review + 1))
      review_hits="${review_hits}
      ${out_rel}"
      do_rewrite=0
    else
      do_rewrite=1
    fi

    [ "$kind" = 'promoted' ] && n_promoted=$((n_promoted + 1)) || n_shared=$((n_shared + 1))

    if [ "$WRITE" -eq 1 ]; then
      mkdir -p "$dest/$(dirname "$out_rel")"
      cp -p "$ROOT/$f" "$dest/$out_rel"
      [ "$do_rewrite" -eq 1 ] && rewrite_paths "$dest/$out_rel" "$promoted"
    fi
  done <<EOF
$files
EOF

  note "shared     : $n_shared"
  note "promoted   : $n_promoted   (from $promoted/)"
  note "dropped    : $n_dropped"
  note "collisions : $n_collide"
  [ -n "$collisions" ] && printf '%s\n' "$collisions"
  note "copied verbatim, NEEDS REVIEW : $n_review"
  [ -n "$review_hits" ] && printf '%s\n' "$review_hits"

  if [ "$WRITE" -eq 1 ]; then
    check_contamination "$target" "$dest"
  fi
}

# ---------------------------------------------------------------------------
# Cross-contamination check — the verification ADR-0006 asks for: no frontend path in the
# backend repository, and no backend path in the frontend repository.
# ---------------------------------------------------------------------------
check_contamination() {
  target="$1"; dest="$2"

  [ -d "$dest" ] || die "nothing to check at '$dest' — run with --write first."

  case "$target" in
    be) forbidden_dir='src/frontend'; forbidden_marker='package.json'; other='frontend' ;;
    fe) forbidden_dir='src/backend';  forbidden_marker='*.csproj';     other='backend'  ;;
  esac

  problems=0

  if [ -d "$dest/$forbidden_dir" ]; then
    printf '  CONTAMINATION: %s exists in the %s repository.\n' "$forbidden_dir" "$target" >&2
    problems=$((problems + 1))
  fi

  # A stray marker file from the other stack anywhere in the tree.
  strays="$(find "$dest" -name "$forbidden_marker" -not -path '*/node_modules/*' 2>/dev/null | head -n 5 || true)"
  if [ "$target" = 'be' ] && [ -n "$strays" ]; then
    printf '  CONTAMINATION: %s file(s) found in the backend repository:\n%s\n' "$other" "$strays" >&2
    problems=$((problems + 1))
  fi
  if [ "$target" = 'fe' ] && [ -n "$strays" ]; then
    printf '  CONTAMINATION: %s file(s) found in the frontend repository:\n%s\n' "$other" "$strays" >&2
    problems=$((problems + 1))
  fi

  # The shared harness must survive derivation intact. If it did not, the three
  # repositories have already diverged and the whole exercise is pointless.
  for shared in .claude/hooks/secret-scan.sh .claude/settings.json .editorconfig .gitattributes .gitignore; do
    if [ ! -e "$dest/$shared" ]; then
      printf '  MISSING SHARED FILE: %s\n' "$shared" >&2
      problems=$((problems + 1))
    fi
  done

  if [ "$problems" -gt 0 ]; then
    printf '  %s check FAILED with %s problem(s).\n' "$target" "$problems" >&2
    return 1
  fi
  note "contamination check: clean"
  return 0
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
printf 'derive.sh\n'
printf '  source : %s  @ %s%s\n' "$ROOT" "$SOURCE_SHA" "$SOURCE_DIRTY"
printf '  output : %s\n' "$OUT_BASE"
if [ "$CHECK" -eq 1 ]; then
  printf '  mode   : CHECK (verifying an existing derivation, writing nothing)\n'
elif [ "$WRITE" -eq 1 ]; then
  printf '  mode   : WRITE\n'
else
  printf '  mode   : DRY RUN — nothing will be written. Add --write to produce the trees.\n'
fi
if [ "$INCLUDE_UNTRACKED" -eq 1 ]; then
  printf '  files  : tracked + untracked-but-not-ignored (--include-untracked)\n'
else
  printf '  files  : tracked only\n'
  if [ "${UNTRACKED_COUNT:-0}" -gt 0 ]; then
    printf '\n  WARNING: %s file(s) are untracked and will NOT be derived.\n' "$UNTRACKED_COUNT"
    printf '  Commit them first, or pass --include-untracked to derive from the working tree.\n'
  fi
fi

rc=0

if [ "$CHECK" -eq 1 ]; then
  [ "$ONLY" = 'fe' ] || { printf '\n=== checking aj-boilerplate-be ===\n'; check_contamination be "$OUT_BASE/aj-boilerplate-be" || rc=1; }
  [ "$ONLY" = 'be' ] || { printf '\n=== checking aj-boilerplate-fe ===\n'; check_contamination fe "$OUT_BASE/aj-boilerplate-fe" || rc=1; }
else
  [ "$ONLY" = 'fe' ] || derive be || rc=1
  [ "$ONLY" = 'be' ] || derive fe || rc=1
fi

if [ "$WRITE" -eq 1 ] && [ "$rc" -eq 0 ]; then
  cat <<'EOF'

MANUAL STEPS — the script cannot do these and does not pretend to.

  1. README.md in each derived tree is the FULL-STACK readme, copied verbatim. It
     describes two stacks and links to paths that no longer exist. Rewrite it, or
     replace it with the promoted stack readme (kept alongside as README.be.md /
     README.fe.md).
  2. CLAUDE.md: the root file and the promoted stack file are both present. ADR-0006
     says they merge. Merge them by hand — a mechanical concatenation produces a
     document that contradicts itself in two places.
  3. .github/workflows/supply-chain.yml and .github/dependabot.yml were copied without
     path rewriting. Both name `src/backend` and `src/frontend` explicitly and need the
     other stack's entries removed rather than its paths rewritten.
  4. Check every remaining `src/backend` / `src/frontend` reference:
       grep -rn 'src/\(backend\|frontend\)' <derived-tree> --exclude-dir=node_modules
  5. Nothing here is a git repository. `git init`, review the whole diff against the
     published repository, and push deliberately.
EOF
fi

if [ "$WRITE" -eq 0 ] && [ "$CHECK" -eq 0 ]; then
  printf '\nDry run complete. Nothing was written. Re-run with --write to produce the trees.\n'
fi

exit "$rc"
