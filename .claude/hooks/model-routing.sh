#!/usr/bin/env bash
# model-routing.sh — UserPromptSubmit
#
# Makes .claude/model-routing.md actually fire instead of merely existing.
#
# UserPromptSubmit is one of the few hook events whose stdout is injected into
# the model's context, so a plain `printf` here is the deterministic way to put
# the routing policy in front of the model on EVERY prompt. A linked markdown
# file is advisory; this is not.
#
# NEVER BLOCKS. UserPromptSubmit *can* block (exit 2 erases the prompt), and a
# routing reminder that halts work would be worse than no reminder at all. Every
# path below exits 0.
#
# Deliberately short: this is injected on every single prompt, so a wall of text
# would burn context and train the reader to skip it. The detail stays in
# .claude/model-routing.md, which this points at.
#
# No jq, no python, no git, no network. Safe to run outside a repository, safe
# to run by hand, and safe to run a thousand times — it reads nothing and writes
# nothing.
#
# OPT-OUT:
#   AJ_SKIP_MODEL_ROUTING_HOOK=1   silences it entirely.

set -u

# Drain stdin so the caller never sees a broken pipe. The payload is not needed:
# the policy is the same for every prompt, and parsing JSON would add a
# dependency for no gain.
cat >/dev/null 2>&1 || true

[ "${AJ_SKIP_MODEL_ROUTING_HOOK:-0}" = "1" ] && exit 0

cat <<'EOF'
<model-routing-policy enforcement="every-prompt">
Classify this task BEFORE any tool call or code edit, and name the cheapest tier that fits.
  FRONTIER (Opus-class) — architecture and design decisions, security review and threat
    modelling, complex multi-system debugging, high-blast-radius refactors, final pre-push
    review.
  WORKHORSE (Sonnet-class) — implementation and CRUD, tests, frontend and UI, database
    migrations, static-analysis fixes, docs and config, routine search.
Then, in your first reply, before starting the work:
  1. State it: "Task: <type> — cheapest sufficient tier: <FRONTIER|WORKHORSE>."
  2. If this session is on a costlier model than the work needs, STOP and say so. You cannot
     switch your own model — recommend the switch and wait for the user.
  3. If the session already matches, say so in one clause and carry on. No ceremony.
  4. Dispatching subagents? Give each the tier ITS task warrants, not the one you are on.
Full policy: .claude/model-routing.md   ·   Silence: export AJ_SKIP_MODEL_ROUTING_HOOK=1
</model-routing-policy>
EOF

exit 0
