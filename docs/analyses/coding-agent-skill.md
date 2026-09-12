# Concept: a Claude Skill for building against Papuma Kernel

Status: **idea/not decided** (2026-08-11). Companion to
[docs/ai/](../ai/) (the current coding-agent doc set, just updated for
`Papuma.Kernel.Local` — see the [SQLite sibling doc](local-kernel-sqlite-sibling.md))
and to [papuma-kernel-slice-conventions.md](../ai/papuma-kernel-slice-conventions.md),
which already specifies a generator contract this concept would partially
implement rather than invent.

Origin: raised while reviewing the just-updated `docs/ai/` set — "would a
Claude Skill be useful for an agent building against Papuma?"

**The question this document answers:** not "is a Skill a good idea in the
abstract" (obviously a Skill *can* be built — that's not in question), but
*where specifically* it would earn its keep over what `docs/ai/` already
does, and where it wouldn't. Skills are cheap to build and easy to
over-scope; the goal here is to find the smallest version that pays for
itself, not to design a maximal one.

---

## 1. What already exists, and what a Skill mechanically adds

`docs/ai/` today: [llms.txt](../../llms.txt) as an index, a
[playbook](../ai/papuma-kernel-playbook.md) (mental model + hard rules +
decision tree + troubleshooting), an
[AGENTS.md snippet](../ai/papuma-kernel-agents-snippet.md) meant to be copied
into a consuming app's `AGENTS.md`/`CLAUDE.md`, and
[slice-conventions.md](../ai/papuma-kernel-slice-conventions.md) for scaffolding.
Claude Code reads `AGENTS.md`/`CLAUDE.md` automatically at session start — so
once a Papuma app has the snippet in place, the mental model and the ~10
binding rules are *already* in every session's context, with no invocation
step and no risk of the agent failing to reach for them.

A Skill's mechanism is different: a short description sits in an
always-visible index; the full instructions (and any bundled scripts/files)
load only when Claude judges the current task matches — *progressive
disclosure*, in Anthropic's own framing.

The comparison that matters: the AGENTS.md snippet is **always loaded, small,
static**. A Skill is **conditionally loaded, can be much larger, can execute
code**. These are different tools for different content — not two
implementations of the same thing.

## 2. Where a Skill would clearly pay for itself

**2.1 — On-demand depth that doesn't belong in every session's fixed context.**
The recipes (`docs/recipes/*.md`), the playbook's troubleshooting
section, the full decision-tree — genuinely useful, but wrong to inline into
every app's `AGENTS.md` permanently. A Skill triggered by "building a
workflow/saga" or "diagnosing feed lag" would pull in exactly the relevant
recipe instead of nothing (agent has to know to go look) or everything
(bloats every session regardless of relevance).

**2.2 — The scaffolding generator that's currently a spec, not a tool.**
This is the strongest case. `slice-conventions.md` states outright: *"A
generator is an external tool that consumes this contract."* It defines,
precisely, which of a slice's six files are mechanical (DTO, Handler,
Endpoint, Registration, Test scaffold — generated from slice name + document
type) and which is the one genuinely variable piece (the Decider). No such
generator exists today; an agent following the doc by hand is re-deriving
mechanical boilerplate from a spec every time. A Skill with a bundled script
turns that spec into an actual tool instead of documentation an agent
re-interprets on each use — the exact case Skills are for (repeatable,
scriptable, currently done freehand).

**2.3 — Branching on which kernel the app uses.**
The `AGENTS.md` snippet already had to split into two variants (Postgres /
SQLite) because enough rules differ (RLS vs. none, SQL-view read lens vs.
none, table naming). A Skill's instructions can branch the same way *inside
one invocation*, driven by what's actually in the consuming app's
`.csproj`/DI setup, rather than requiring the human to have picked and pasted
the right static block correctly.

## 3. Where it would not help, and the real risks

- **Content-fork risk.** A Skill's bundled reference material must not
  become a second, hand-maintained copy of `docs/ai/` and the guides that
  quietly drifts. Given how much documentation this project already
  maintains in parallel (canonical English, an archived German snapshot, the
  NuGet-embedded copy, the GitHub raw-URL copy `llms.txt` points at), adding
  a fifth surface without a single source of truth is a real cost, not a
  hypothetical one.
- **Claude-only reach.** A Skill only helps inside Claude Code / claude.ai /
  the Agent SDK. `docs/ai/` has to keep working standalone for any other
  agent or a human reading raw Markdown — a Skill is additive, never a
  replacement for the doc set actually being correct and complete on its
  own.
- **Discovery is a judgment call, not a guarantee.** The AGENTS.md snippet's
  "always loaded" property is a feature for the ~10 hard rules specifically
  *because* missing one of them is exactly the kind of mistake this project
  has been careful to make structurally hard (ADR-002/003/007). Rules that
  matter every time should stay in the always-loaded snippet; only the
  genuinely occasional content belongs behind a Skill's relevance judgment.
- **Not obviously worth it yet for the "recipes as Skill" idea (§2.1) specifically.**
  The playbook + snippet together are still small (well under 200 lines
  combined). There's no evidence today that context budget is actually a
  problem for a Papuma-consuming app — building a Skill to solve a budget
  problem that hasn't been observed would be solving a hypothetical. Worth
  revisiting once there's a concrete app where it's felt, not before.

## 4. Proposed shape, if built

Scope it to the one case with unambiguous payoff (§2.2) rather than all
three at once:

- **One Skill**, not two — branch on kernel inside the `SKILL.md`/scaffold
  script (detected from the consuming app's package references) rather than
  shipping `papuma-kernel` and `papuma-kernel-local` as separate Skills the
  agent has to pick between.
- **`SKILL.md`**: a short trigger description ("use when adding or modifying
  a Papuma Kernel feature slice"), the compressed decision tree from the
  playbook, and links back to `docs/ai/` and `docs/recipes/` for anything
  deeper — reference, not duplicate.
- **A bundled scaffold script** implementing `slice-conventions.md`'s
  generator contract: slice name + document type in, the five invariant
  files out, Decider left as a stub for the human/agent to fill. This is the
  part that's actually new capability, not repackaged documentation.
- **Distribution**: a copyable template (e.g. `templates/claude-skill/` in
  this repo, or alongside the `AGENTS.md` snippet in `docs/ai/`), copied into
  a consuming app's `.claude/skills/papuma-kernel/` the same way the snippet
  is copied into `AGENTS.md` today. No packaging/registry mechanism is
  assumed to exist for this yet.
- **Explicitly deferred**: §2.1 (recipes-as-Skill) and any static-analysis/
  lint companion (e.g. flagging a `SaveAsync` without `expectedVersion`, or a
  document type touching PII without a policy) — both are reasonable later
  additions, neither has a concrete need behind it today.

## 5. Recommendation

Build the scaffold-script Skill (§2.2/§4) first, as a standalone, small
addition — it's the one piece with a clear "spec exists, tool doesn't" gap
and a bounded scope. Leave §2.1 (recipes as on-demand Skill content) and any
lint/static-analysis companion as explicitly future, revisited only if a
concrete Papuma-consuming project surfaces the need.

## 6. Open questions

- **Kernel detection inside the scaffold script** — read the consuming app's
  `.csproj` for `Papuma.Kernel` vs. `Papuma.Kernel.Local`, or ask? (Leaning
  detect-first, ask-if-ambiguous.)
- **Where the reference content inside `SKILL.md` is sourced from** —
  hand-authored once and manually kept in sync with `docs/ai/`, or generated/
  fetched from it at build time? Hand-authoring is simpler to start but is
  exactly the fork risk in §3; worth deciding before writing anything, not
  after.
- **Versioning** — does the Skill template track the kernel's version number,
  or stay version-agnostic (only the public API shape, which is more stable
  than the version number)?

None of these block starting on §4's scoped version; they matter once it
exists and needs maintaining.
