# Security policy

This repository is a boilerplate. People clone it and then own the clone. That single fact
shapes everything below, so read the supported-versions section before you read the reporting
section — it explains what a fix here can and cannot do for you.

---

## Supported versions

"Supported" here means one thing only: **we will fix the defect on `main` and cut a new tag.**
It does not mean a deployed service is patched, because there is no deployed service. It does
not mean a package is republished, because nothing is published to a registry.

| Version | Supported | What that means in practice |
|---|---|---|
| Latest tag on `main` | Yes | Fixed on `main`, new tag cut |
| Any older tag | No | Not patched, not backported, not revoked |
| Your clone | No | Yours from the moment you cloned it |

Only the most recent tagged release is supported. Older tags are historical artefacts. They are
not backported to, and they are not withdrawn either — an old tag with a known vulnerability
stays where it is, because deleting it would break every consumer who pinned to it and would
silently rewrite the record of what was shipped.

### The uncomfortable consequence

**A fix landed here does not reach anyone who has already cloned.** There is no update channel.
There is no dependency resolver that will notice. A clone is a copy, and copies do not track
their origin.

If you cloned this repository, patching your own tree is your responsibility, and it is a
deliberate act: read the fix, decide whether it applies to how you have since changed the code,
and port it. The mechanics of pulling boilerplate changes into a diverged clone are in
[docs/upgrading.md](docs/upgrading.md). Read that before you need it, not after.

This is not a gap we intend to close. A boilerplate that could push changes into your codebase
would be a framework, and this deliberately is not one.

---

## Reporting a vulnerability

Use GitHub's **private vulnerability reporting**. It opens a private security advisory that only
you and the maintainers can see.

**Security** tab → **Report a vulnerability** → fill in the advisory form.

Or go directly to:

```
https://github.com/<owner>/<repo>/security/advisories/new
```

Replace `<owner>/<repo>` with this repository's real path. It has been left as a placeholder
because this file is published in three derived repositories and hard-coding one of them would
be wrong in the other two.

**Do not open a public issue for a vulnerability.** A public issue is a disclosure, and it is a
disclosure made before anyone has had a chance to fix the thing you found.

### Why there is no contact email here

There is deliberately no security email address in this file. An address published in a public
repository is scraped within days and receives more automated rubbish than reports. It also ages
badly — the person behind it changes role, the alias is never updated, and a report lands
nowhere at all with no bounce. Worst of all, plain email offers no confidentiality guarantee: it
crosses relays in the clear, sits in mailboxes, and gets forwarded.

The advisory mechanism has none of those problems. It is private, it is authenticated, it is
tracked against the repository, it lets a maintainer open a private fork to develop the fix, and
it produces a published advisory with a CVE at the end.

### If you cannot see the option

Private vulnerability reporting must be **enabled by the repository owner**, under
**Settings** → **Code security** → **Private vulnerability reporting**. If the Security tab shows
no way to report, that is the reason — the feature is off, not missing.

If you are the repository owner reading this: turn it on before you make the repository public.
Publishing a security policy that points at a disabled feature is worse than publishing none,
because a reporter who cannot find the private route will use the public one.

### What to put in the report

The more of this you can supply, the faster the assessment:

- **Affected file or path**, as specific as you can be — ideally `path/to/file.cs:120`.
- **Version or commit SHA** you found it in. A tag name is fine; a SHA is better.
- **Reproduction steps.** A minimal sequence that shows the behaviour. A proof-of-concept diff
  or script is ideal.
- **Impact.** What does an attacker get, and what do they need in order to get it? Say plainly
  whether it needs authentication, a particular role, network position, or a specific
  configuration.
- **Whether it is already public.** If it is already on a mailing list, in a blog post, or in
  someone else's advisory, say so immediately — it changes the disclosure timetable entirely.

Speculation is welcome, but label it as such. A report that says "I think this is exploitable but
I could not build a proof of concept" is still useful and is much better than silence.

---

## What we commit to

These are **targets**, not a service level agreement. This repository has no on-call rota and no
paid support contract behind it. A maintainer should confirm these numbers are achievable before
this file goes public, and change them if they are not. Numbers nobody can meet are worse than
honest slower ones.

| Stage | Target |
|---|---|
| Acknowledge the report | 3 working days |
| Initial assessment — is it a vulnerability, and how severe | 10 working days |
| Fix, or explain why we will not fix | Communicated with the assessment |

"Explain why we will not fix" is a real outcome, not a euphemism for silence. Some reports
describe intended behaviour, some are out of scope (see below), and some are real but not
economically fixable in a boilerplate. In each case you get the reasoning, not a closed ticket.

---

## Coordinated disclosure

Please do not disclose publicly until a fix is tagged, or until an agreed period has elapsed and
we have failed to ship one. If we go quiet, that is a failure on our side and you are entitled to
publish; tell us you intend to and give us a date.

You will be **credited in the published advisory** by whatever name or handle you ask for, unless
you would rather not be. Say so in the report either way. We do not run a bounty programme and
have no money to offer; credit is the whole of what we can give.

---

## Scope

### Out of scope

A boilerplate ships deliberately unfinished. The following are not vulnerabilities in this
repository, however they may look in a scanner report:

- **Anything that requires the consumer's own deployment configuration.** How your cluster is
  networked, which identity provider you wired up, what your ingress allows.
- **The deliberately unconfigured placeholders** — empty authority URLs, blank secret values in
  `appsettings.json`, unset repository variables. They are empty because a value there would be
  either fake or dangerous, and no value can be right for every consumer.
- **Missing rate limits, WAF rules, or DDoS protection on a service nobody has deployed.** These
  are properties of a deployment, not of source code.
- **Anything in `infra/` that is expected to be configured before the first `apply`.** Those
  trees ship with no state backend and no real project identifiers, by design.
- **Findings from a scanner run against the sample `Item` entity or the demo mock data.** They
  are demonstration scaffolding and are meant to be deleted.
- **Dependency advisories with no exploitable path**, where the fix is a version bump. Those are
  handled by Dependabot and the dependency-audit gates; report them as ordinary issues.

### In scope, and often missed

**A placeholder that is easy to miss and dangerous to leave unset is in scope.** That is a real
defect in a boilerplate, and it is the class of bug this project is most likely to ship.

If a value defaults to something insecure rather than to something that fails loudly; if a
security control is off by default and nothing tells you; if the documentation does not mention a
setting that must be changed before a production deployment — report it. "You were supposed to
configure that" is not a defence when nothing said so and nothing failed when you did not.

Also in scope: anything that would be a vulnerability in the shipped code paths regardless of
configuration — injection, broken authorization logic, unsafe deserialisation, a secret written
to a log, a token accepted without validation, an architecture test that claims to enforce a
boundary it does not.

---

## What already runs

Reporting is the last line, not the first. These gates run automatically, and a report that one
of them already catches will be closed as a duplicate of the gate.

| Gate | Where |
|---|---|
| **Gitleaks** secret scanning | `backend-ci.yml`, `frontend-ci.yml`, and locally in `.claude/hooks/secret-scan.sh`. Config in [`.github/gitleaks.toml`](.github/gitleaks.toml) |
| **CodeQL** code scanning | `backend-ci.yml` (C#) and `frontend-ci.yml` (TypeScript), `security-and-quality` query suite |
| **`dotnet list package --vulnerable`** | `backend-ci.yml`, including transitive packages, failing at Moderate and above |
| **`npm audit`** | `frontend-ci.yml`, failing at High and above |
| **Container image scanning and SBOM generation** | [`.github/workflows/supply-chain.yml`](.github/workflows/supply-chain.yml) |
| **Dependabot** version and security updates | [`.github/dependabot.yml`](.github/dependabot.yml) |

The local hook matters as much as the CI gates: `secret-scan.sh` runs on every `Edit` and `Write`
and refuses the change, so a credential is caught before it is ever written rather than after it
is pushed. CI catching a secret is already too late — by then it is in the history.

None of this makes the code secure. It makes a specific set of mistakes harder to ship, which is
a different and much smaller claim.
