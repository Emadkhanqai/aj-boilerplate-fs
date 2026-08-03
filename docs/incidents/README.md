# Incident reports

An incident report exists so that the next person to meet this failure mode finds it in ten
minutes instead of rediscovering it over two days. That is the whole justification. It is a
**search index for past pain** — something a future engineer greps when a symptom looks familiar,
or stumbles into from `git blame` on the line that once broke.

It is not a compliance artefact, not a status update for management, and not a record of who was
at fault. A report written to satisfy a process gets written defensively, and a defensive report
omits exactly the detail that would have been useful: the wrong hypothesis, the misleading log,
the four hours spent in the wrong layer.

Reports are **blameless**. They name systems, changes, and gaps — never a person as a cause. See
the note in [TEMPLATE.md](TEMPLATE.md).

---

## When to write one

Write one for any of these, without discussion:

- Anything that reached a deployed environment and degraded it — including staging.
- Any data loss, corruption, or unintended exposure, however small. "Only three rows" is still a
  report.
- Any security event: a leaked credential, an authorization bypass, a dependency advisory that was
  actually exploitable here.
- **Any gate that passed something it should have blocked.** A gate you cannot trust is a worse
  problem than the bug that got through it.
- Any rollback.
- Anything that cost more than roughly half a day to diagnose, even if the fix was one line.
  Especially then — the diagnosis time is the reusable part.

The last one is the bar most teams set too high. If it was hard to find once, it will be hard to
find again.

## When not to write one

- A failure caught in CI. That is the gate working.
- A mistake that never left your machine.
- A red quality gate, a blocked hook, a refused push. Those are the controls doing their job, and
  a report about them is a report about the system behaving correctly.
- A known, already-documented failure recurring. Link the existing report and add to it instead.

This restraint is load-bearing. Writing a report for everything is how a directory becomes
unreadable, and an unreadable directory is not searched. The value of these files is entirely a
function of the signal-to-noise ratio in them.

---

## Naming

`YYYY-MM-DD-short-slug.md`, dated by when the incident **started**, not when it was written.

```
2026-02-14-item-list-timeout-under-page-size.md
2026-03-02-migration-dropped-owner-email.md
```

The slug should describe the failure, not the fix, because the failure is what a future reader
will be searching for.

## The process

1. **Draft within 48 hours.** Not because of a policy, but because the recoverable detail decays
   fast: logs roll off, dashboards lose resolution, and nobody remembers which hypothesis came
   third. Draft while it is still recoverable, even if the follow-up work is not finished.
2. **Review with someone who was not involved.** The author cannot see what they assumed. The
   reviewer checks that the root cause explains the symptom, that Verification is evidence rather
   than assertion, and that every lesson is an owned action.
3. **Mark it `Reviewed`.** A `Draft` is not something to link people to as an explanation.
4. **Link it from the follow-up issues**, and link the issues from the report. Otherwise the
   follow-up table is a wish list nobody revisits.

---

## How this relates to the rest of `docs/`

An **[ADR](../adr/README.md)** records a decision; an incident records a failure. They meet
regularly: an incident that exposes a structural problem should produce an ADR, and the incident
report is then the ADR's Context section written out in full. Link them both ways when that
happens.

A **[spec](../specs/TEMPLATE.md)** describes what should happen; an incident describes what did.

The **[Definition of Done](../definition-of-done.md)** is what the Detection gap section is
measured against — condition 2 lists every gate that could have caught the failure, and condition
5 is the staging smoke test that so often did not happen. An incident whose detection gap is
"nobody exercised the changed path on staging" is a Definition of Done failure, not a testing
failure, and should be written up as one.

---

## This directory ships empty

It contains a template and this page and nothing else. That is the correct starting state: a
boilerplate has no incidents, and inventing example reports would put fictional failures into the
search index that a real one is supposed to be.

Your first report will be a real one. Copy [TEMPLATE.md](TEMPLATE.md) and keep every heading.
