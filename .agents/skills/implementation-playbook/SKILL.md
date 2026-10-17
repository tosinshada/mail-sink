---
name: implementation-playbook
description: Write an implementation playbook — a directive, decisions-already-made execution guide aimed at a coding agent that will implement a large change in one pass. Produces a doc with fixed decisions ("do not re-litigate"), invariants that must not change, a per-file implementation guide with target signatures and old→new mapping tables, guidance for judgment calls where exact parity is impossible, an explicit out-of-scope list, a verification checklist, deferred follow-ups, and an appendix of API facts verified against installed source. Trigger on "write an implementation plan/playbook/guide for", "turn this design into something an agent can execute", "migrate X to Y and write it up so Claude can do it", "companion to the feasibility doc", or when a library swap, framework migration, or large mechanical refactor is already decided and the remaining work is translation. Writes to `specs/<id>/IMPL_PLAYBOOK.md`. Do NOT use when the design is still open (use design-plan instead) or for a routine small change.
---

# Implementation playbook

## What this produces

A single markdown document that hands a large, already-decided change to a coding agent and
gets it implemented in one pass, correctly, without the agent re-deciding anything or building
scaffolding nobody asked for.

The reader is an implementer with no memory of the discussion that produced the decisions and
a strong instinct toward hedging — parallel implementations, compatibility shims, adapters,
test harnesses. Most of this document's structure exists to shut those instincts down: fixed
decisions so nothing is re-argued, invariants so nothing drifts, an out-of-scope list that
names the specific temptations, and a verification section that says what "done" means so the
agent doesn't invent its own bar.

A playbook is written *after* the design is settled — often as the companion to a feasibility
report or a design plan. Say so in the preamble and link it.

## Output location

Always write to `specs/<id>/IMPL_PLAYBOOK.md`, relative to the repository root.

`<id>` is a short kebab-case feature name — three or four words, naming the change rather than
the ticket: `specs/pi-ai-migration/IMPL_PLAYBOOK.md`,
`specs/vertical-tabs-hover-sidecar/IMPL_PLAYBOOK.md`.

- **Check for an existing `specs/` directory for this change first.** If a design plan already
  exists at `specs/<id>/DESIGN_PLAN.md`, reuse that exact `<id>` — the two documents belong in
  the same directory — and read the design plan before writing anything. Its resolved
  questions and phasing are the source of your fixed decisions, and the preamble should point
  at it by path.
- Otherwise pick `<id>` yourself and confirm it alongside the Phase 3 framing questions rather
  than asking separately.
- If `specs/<id>/IMPL_PLAYBOOK.md` already exists, you are revising it — read it first.
- Anything else the work produces (feasibility notes, captured API research too long for the
  appendix) goes in the same directory.
- Create the directory if it doesn't exist. Don't invent a different root — not `docs/`, not
  `plans/`, not `.claude/`.

## When not to use this

- **Design still open** — any "should we..." left unanswered → use the `design-plan` skill,
  which writes `specs/<id>/DESIGN_PLAN.md` alongside this document.
  Do not smuggle open questions into a playbook as "the implementer decides"; that's how you
  get an adapter layer nobody wanted.
- **Small change.** If the diff is one file, write the instruction, not a document.
- **Behavior spec for a new user-facing feature** → product spec.

## Process

### Phase 1 — Establish ground truth, and say where it lives

The single highest-value thing a playbook does is point the implementer at authoritative
source instead of letting it work from memory of an API.

- Locate the real reference: installed packages under `node_modules/**/dist/**`, the vendored
  source tree, the generated types. Prefer unbundled, unminified dist with doc comments
  preserved, and `.d.ts` for signatures. Name the exact paths in the document.
- Read enough of it to verify every claim you plan to make. When you assert what a function
  returns, or that a library never throws, or that a header is or isn't forwarded — you read
  that code.
- Record what you verified into the appendix (§9 in the template), and mark it as a snapshot:
  *the installed source is authoritative; re-verify before relying on these.*
- Where you could not verify something, say so inline at the point of use with a fallback:
  "(Exact signature/order per source — verify. If it rejects our context shape, fall back to
  `agentLoop` with the tail split out.)" Honest uncertainty with a plan beats confident
  wrongness.

### Phase 2 — Inventory the existing implementation

Walk the code being replaced and note, by symbol name: every call site, every type that
crosses the boundary, every behavior that exists for a reason someone will not remember
(caching strategy, statelessness flags, error classification, retry policy, header capture).

For each, decide and record one of: **preserved exactly** (goes in Invariants), **translated**
(goes in the per-file guide, with a mapping), or **deleted** (say so explicitly, by name — an
implementer will not delete code unless told to).

### Phase 3 — Confirm the framing decisions

Even a settled change has framing choices the user must confirm before you write. Use
`AskUserQuestion` for the ones that are genuinely theirs:

- **Regression tolerance.** Is this early alpha where minor behavioral drift is acceptable and
  gets fixed as discovered, or does it need parity? This single answer changes the whole tone.
- **Commit shape.** One commit, or a small stack? Must intermediate states build?
- **Scope boundary.** Which adjacent improvements are explicitly Phase 2?
- **Pre-authorizations.** Is the implementer allowed to add a compat flag, change env vars,
  delete a test suite? Say yes or no in advance; anything unstated becomes a stall.

### Phase 4 — Write

Follow `reference/template.md` for section order and `reference/house-style.md` for voice.
Read both first; they are short.

### Phase 5 — Adversarial review

Reread as the implementing agent and hunt for the three failure modes:

1. **A place where I'd have to decide something.** Every one is a bug in the playbook. Either
   decide it, or add it to the judgment-calls section with a rule ("do the most reasonable
   thing; match today's behavior in spirit, not in detail").
2. **A place where I'd build something defensive.** Would I add a fallback to the old path
   here? A feature flag? A shim? If yes, and it's unwanted, name it in the out-of-scope list.
3. **A place where I'd invent scope.** Would I write tests for this? Refactor this while I'm
   here? Say what test work is required and that nothing beyond it is.

Then run the checklist in `reference/house-style.md`.

## Non-negotiables

- **Every decision is stated as settled**, in a numbered list headed by an instruction not to
  re-litigate it.
- **Deletions are named.** "Delete today's `prepareStep` strategy." "`captureAiGatewayLogId`
  middleware and all `ai-gateway-provider` imports are deleted." An implementer will preserve
  anything you don't explicitly kill.
- **Invariants list what has zero diff**, ideally at package granularity: "this package should
  have **zero diff**".
- **Line references are marked approximate and paired with symbol names**, with a standing
  instruction to locate by symbol.
- **Old→new translation uses a table** when there are more than three shapes to map.
- **Verification is bounded**: the commands that must pass, plus a numbered manual smoke
  checklist. State plainly that nothing beyond it is required — no parity harness, no new
  automated suites — unless it is.
- **Follow-ups are listed as separate commits/PRs**, so deferral reads as scheduling rather
  than omission.

## Reference files

- `reference/template.md` — the nine-section skeleton with the shape and failure mode of each.
- `reference/house-style.md` — voice rules and the pre-delivery checklist.
