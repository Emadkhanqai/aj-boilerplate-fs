# Day-1 onboarding

Welcome. This is one day's work, in order. Do not skip ahead — step 4 is a checkpoint, not a
formality, and the point of step 5 is to have shipped something small before you are asked to
ship something large.

By the end of the day you will have a green gate on your machine and one merged change.

---

## 1. Install and authenticate Claude Code

```bash
npm install -g @anthropic-ai/claude-code
claude          # follow the prompts to authenticate
claude --version
```

This repository is designed to be driven with Claude Code. Everything here works without it —
the commands are ordinary `dotnet` and `nx` commands — but the guardrails in `.claude/` only
apply when you are using it.

### Plugins

The harness depends on a set of published plugins. `.claude/settings.json` declares them, so an
interactive session in this repository will offer to install them on first run. Accept.

If you would rather do it explicitly, or the prompt did not appear:

```bash
# inside an interactive `claude` session, in the repository root
/plugin marketplace add obra/superpowers
/plugin marketplace add anthropics/skills
/plugin marketplace add wshobson/agents
/plugin marketplace add multica-ai/andrej-karpathy-skills
/plugin marketplace add mattpocock/skills

/plugin              # browse and install from the marketplaces you just added
```

Verify with `/plugin` — the installed plugins should be listed and enabled. If a marketplace
fails to add, check that `git` can reach GitHub from your machine before debugging anything else.

---

## 2. Clone and set up

```bash
git clone https://github.com/<your-org>/<this-repo>.git
cd <this-repo>
```

**Prerequisites:** Docker. That is all you need to run everything. To build, test or debug outside
Docker you also need the **.NET SDK 10** and **Node.js 22**.

```bash
cp .env.example .env          # then set MSSQL_SA_PASSWORD to any strong local value
docker compose up --build     # SQL Server, Redis, Keycloak, migrations, Auth service, API, web
```

Wait for it to settle (the first run imports the Keycloak realm), then open
<http://localhost:4200> and sign in as `admin`, `editor` or `viewer` (password in the root
README). No Microsoft tenant and no secrets from your tech lead are needed: the local stack uses a
bundled Keycloak with committed, dev-only values. Also try `noaccess`, to see what a user with no
role gets.

Open **Items** and create, edit and delete a row. Seeing the sample feature work end to end now
means that when something breaks later you know it was you.

If you are stuck, [docs/auth/troubleshooting.md](auth/troubleshooting.md) covers the usual
suspects (a stale SQL volume, a port in use, Keycloak not ready).

**Working on one stack without Docker:**

```bash
# backend: the tool manifest and the SDK pin live in src/backend, so run EF from there
(cd src/backend && dotnet tool restore && dotnet ef database update \
    --project src/AjBoilerplate.Infrastructure --startup-project src/AjBoilerplate.Api)
dotnet run --project src/backend/src/AjBoilerplate.Api    # http://localhost:5080, 401 without Keycloak settings

# frontend: no backend needed
cd src/frontend && npm ci && npm run dev:offline          # http://localhost:4200, mocked API, role picker
```

The sign-in flow itself only runs in the compose stack. Ask your tech lead whether this project
runs `AUTH_PROVIDER=embedded` (it deploys its own Auth service) or `usp` (inside the Al Jazeera
USP platform), and which cloud (`CLOUD_PROVIDER`) it targets. Real Entra and Keycloak values are
never in the repository; they come from the project's secrets provider.

---

## 3. Read the context files

In this order:

1. **[`CLAUDE.md`](../CLAUDE.md)** at the repository root — the stack, the architecture, the
   layer dependency rule, the commands, and the non-negotiable rules. Read it properly. It is
   short on purpose.
2. **[`src/frontend/CLAUDE.md`](../src/frontend/CLAUDE.md)** if you will touch the frontend. There
   is no backend `CLAUDE.md`; the backend's conventions are in the root file and
   [`src/backend/README.md`](../src/backend/README.md).
3. **[`docs/auth/README.md`](auth/README.md)** — ten minutes, and it explains the part of this
   system that surprises people most: the browser holds no token.
4. **`.claude/standards/`** — skim the index, read in full whichever standards cover what you
   are about to touch.

Note the hard rule while you are there: **never put a secret, connection string, token, or
credential in `CLAUDE.md`, in a prompt, or in any other context file. Ever.**

---

## 4. Get a green gate locally

Before you change anything, prove the baseline works on your machine.

```bash
/qa
```

Or the underlying commands, if you prefer to see them:

```bash
dotnet build src/backend/AjBoilerplate.slnx -warnaserror
dotnet format src/backend/AjBoilerplate.slnx --verify-no-changes
dotnet test  src/backend/tests/AjBoilerplate.UnitTests
dotnet test  src/backend/tests/AjBoilerplate.ArchitectureTests
dotnet test  src/backend/tests/AjBoilerplate.IntegrationTests
cd src/frontend && npx nx affected -t lint,test,build
```

**Everything must be green before you write a line of code.** If it is not, that is today's
first task and your tech lead wants to know — a broken baseline on a new machine is usually a
missing prerequisite, and it is worth fixing in the setup documentation for the next person.

---

## 5. Ship one small change, end to end

Pick something genuinely small with your tech lead — a validation message, a missing empty state,
a small endpoint. Then take it through
[all five stages](workflow.md): **Spec → Plan → Execute → Verify → Review**.

- Write the spec from [the template](specs/TEMPLATE.md), even though the change is tiny. The
  point is the habit.
- Write the failing test first, and watch it fail.
- Run the full gate and paste the output into the pull request.
- Your tech lead pair-reviews it with you — walking through the code together, not approving it
  asynchronously. This review is the actual onboarding; the rest is setup.

Meet [the Definition of Done](definition-of-done.md), all six conditions, on this change. It is
much easier to learn the bar on something small.

---

## 6. Read the last five ADRs and the spec template

```bash
ls docs/adr/
```

Read the five most recent [ADRs](adr/README.md). They tell you what was decided, why, and what
was rejected — which is the context that stops you from re-proposing an alternative the team
already ruled out.

Then read [the spec template](specs/TEMPLATE.md) end to end, including the guidance
blockquotes. It is the shape of every piece of work you will be asked to do here.

---

## Checklist

- [ ] Claude Code installed, authenticated, plugins enabled
- [ ] Repository cloned, database migrated, both stacks running locally
- [ ] Sample feature exercised in the browser
- [ ] Root `CLAUDE.md`, nested `CLAUDE.md`, and the relevant standards read
- [ ] `/qa` green locally
- [ ] One small change shipped through all five stages, pair-reviewed by the tech lead
- [ ] Last five ADRs read
- [ ] Spec template read

---

## Where to ask

Ask early. A question that takes someone five minutes to answer is cheaper than a day spent
guessing, and the things that are obvious to the team are exactly the things nobody remembered to
write down.

| Topic | Look here first |
|---|---|
| How do I build/run/test X? | [`CLAUDE.md`](../CLAUDE.md) |
| Why is it built this way? | [`docs/adr/`](adr/README.md) |
| What is the process? | [`docs/workflow.md`](workflow.md) |
| When can I merge? | [`docs/definition-of-done.md`](definition-of-done.md) |
| How do I change the API contract? | [`docs/api/README.md`](api/README.md) |
| What are the coding standards? | `.claude/standards/` |
| What happened in the last session? | [`docs/handoff/`](handoff/) |
