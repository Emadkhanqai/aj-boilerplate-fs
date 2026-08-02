# The five-stage workflow

Every change — a bug fix, a feature, a refactor — moves through the same five stages:

**Spec → Plan → Execute → Verify → Review**

The stages exist to put the thinking before the typing and the checking before the merging. They
are proportional: a one-line fix spends thirty seconds in Spec and Plan, not thirty minutes. What
is *not* proportional is skipping Verify or Review — those are fixed costs on every change.

---

## Stage 1 — Spec

**Owner:** the developer, reviewed and approved by the tech lead
**Output:** `docs/specs/YYYY-MM-DD-<slug>.md`, status `Approved`
**Command:** `/spec`

Write down the problem, the acceptance criteria as Given/When/Then, the API contract, the data
model change, the UI states, the test plan, and — explicitly — what is out of scope. Use
[the template](specs/TEMPLATE.md); keep every heading.

Nothing is built until the spec is approved. This is the cheapest stage to be wrong in, and the
only one where being wrong costs nothing.

**Done when:** a second person can read the spec and independently describe what will be built,
and every open question in §8 is closed.

---

## Stage 2 — Plan

**Owner:** the developer, with the agent
**Output:** an ordered task list, each task independently testable
**Command:** `/task`

Break the spec into tasks. A good task is one sitting's work, has an observable outcome, and can
be verified on its own. If a task cannot be tested independently, it is two tasks or it is
underspecified.

Decide here what needs an ADR. If the plan contains a decision that is expensive to reverse,
write the ADR now — not after the code makes the decision for you.

**Done when:** the task list is ordered, each task names the tests that will prove it, and the
estimated diff for each is small enough to review.

---

## Stage 3 — Execute

**Owner:** the developer, prompting the agent
**Output:** working code with tests, committed
**Command:** `/implement`

**Test first, always.** Write the failing test. Watch it fail — a test that has never failed has
not been shown to test anything. Then write the minimum code that makes it pass. Then refactor
with the test green.

One task per session. Fresh context for each task. When a task is done, the session ends.

**Done when:** the task's tests pass, the affected suites still pass, and the diff contains
nothing the task did not require.

---

## Stage 4 — Verify

**Owner:** the developer
**Output:** a green local gate, with evidence
**Commands:** `/qa`, then `/pre-push`

Run the full gate locally before asking anyone else to look at the work:

- Build with warnings as errors — a warning is a failure.
- `dotnet format --verify-no-changes`, ESLint, Prettier.
- Unit, integration, and architecture tests.
- The quality gate: zero new Blocker, Critical, or Major findings; ≥80% coverage on new code.
- Playwright, if a user journey changed.

Paste the evidence into the pull request. "It works" is not evidence; the command output is.

If the gate is red, it is not "nearly done". It is not done.

**Done when:** every gate is green locally and the output is in the pull request.

---

## Stage 5 — Review

**Owner:** an AI reviewer *and* a human reviewer — both, in that order
**Output:** an approved, merged pull request
**Commands:** `/review`, then a human

`/review` first: architecture boundaries, standards, security, contract correctness. Fix what it
finds before a human spends time on it.

Then a human reviews every line. Not a skim, not a rubber stamp.

**Human review is mandatory and is never waived because an agent wrote the code.** If anything,
agent-written code needs *more* attention: it is fluent, confident, plausible, and consistently
formatted, which makes a wrong approach look exactly like a right one. Reviewers should be most
careful precisely where the code reads most smoothly.

Read [the Definition of Done](definition-of-done.md) before approving. All six conditions, or
it does not merge.

**Done when:** both reviews are approved, the Definition of Done is met, and there are no open
critical or major findings.

---

## Guardrails

These are not suggestions. They exist because each one has a specific failure it prevents.

### One task per session, fresh context per task

Long sessions accumulate stale context: superseded decisions, abandoned approaches, half-finished
edits. The agent then reasons from a mix of what is true and what used to be true. Start each
task clean.

### Test-driven, genuinely

The failing test comes first and you watch it fail. This is the difference between testing your
code and writing code that agrees with your test. It matters more with an agent, not less — an
agent asked for code and tests together will produce tests that pass against whatever it wrote.

### No unattended multi-hour runs

Never leave an agent running unsupervised for hours. Without a human in the loop, a small wrong
turn compounds into a large one, and by the time you look the diff is unreviewable and the
context that produced it is gone. Stay present, review at each task boundary.

### Roughly 400 changed lines per pull request

A reviewer's attention is finite and measurable, and it falls off a cliff well before a
thousand-line diff. Above roughly 400 changed lines, review quality degrades into
pattern-matching. Split the work. Generated files (`api-types`, migrations) are excluded from
the count — but they are still reviewed.

### The prompting developer owns the code

You wrote it. Not the agent — you. You are accountable for its correctness, its security, its
performance, and its maintenance. "The AI generated it" explains nothing and excuses nothing. If
you cannot explain a line in review, it does not merge.

### Never push without explicit human approval

Committing is routine. Pushing is a decision a human makes, every time, on every branch and
every remote.

### Secrets never enter context

No credential, connection string, token, or key in a prompt, a file, a commit message, a spec,
or an ADR. Ever. Rotate anything that leaks.

### Documentation moves with the change

If a convention changed, `CLAUDE.md` changes in the same pull request. If a decision was made, an
ADR lands with it. If a contract changed, the OpenAPI document and the generated types change
with it. Documentation that lags is documentation that misleads.

---

## Commands

| Command | Stage | What it does |
|---|---|---|
| `/spec` | 1 | Start a spec from the template |
| `/task` | 2 | Break an approved spec into tasks |
| `/implement` | 3 | Implement one task, test-first |
| `/new-migration` | 3 | Create, review as SQL, and apply an EF Core migration |
| `/sync` | 3 | Regenerate API types from OpenAPI and check for duplicates |
| `/qa` | 4 | The full local gate |
| `/quality-gate` | 4 | Static analysis only, enforcing zero Blocker/Critical/Major |
| `/pre-push` | 4 | The gate plus a readiness report — never pushes |
| `/review` | 5 | AI review of the diff |

Hooks in `.claude/hooks/` enforce parts of this automatically: formatting on write, blocking
dangerous shell commands, protecting sensitive files, running affected tests, scanning for
secrets, gating pushes on the quality gate, and writing a session handoff to
[docs/handoff/](handoff/).
