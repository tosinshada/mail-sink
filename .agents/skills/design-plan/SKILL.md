---
name: design-plan
description: Write an "Analysis & Plan" document for a substantial architectural change to an existing codebase — a change that touches storage schemas, public APIs, migrations, or a core concept, and where the design is not yet settled. Produces a doc with a one-paragraph current-state, numbered core decisions with rejected alternatives, a change inventory by area, a numbered log of resolved questions, phased commits with review checkpoints, and an explicit not-implemented list. Trigger on "write a plan for", "design doc", "how should we restructure X", "plan out the migration to Y", "analysis and plan", "I want to add multi-X support", or when the user describes a refactor big enough that the first hard part is deciding what to build, not building it. Writes to `specs/<id>/DESIGN_PLAN.md`. Do NOT use for a single technology choice (that is an ADR), for user-facing product behavior (product spec), or for a change already fully decided (use implementation-playbook instead).
---

# Design plan

## What this produces

A single markdown document that takes a fuzzy "we should restructure X" and turns it into a
settled design: every architectural decision made and justified, every rejected alternative
recorded with its reason, every area of the codebase inventoried for its delta, and the work
cut into commits that a human will review one at a time.

The reader is a senior engineer or a coding agent who will implement it, and — later — the
same people arguing about why it was done this way. The document must survive that second
audience. That is why rejected alternatives and accepted warts are load-bearing content, not
padding.

This is not a proposal. By the time it is written, the decisions are made. Open questions
appear only in the "Resolved questions" log, already answered.

## Output location

Always write to `specs/<id>/DESIGN_PLAN.md`, relative to the repository root.

`<id>` is a short kebab-case feature name — three or four words, naming the change rather than
the ticket: `specs/vertical-tabs-hover-sidecar/DESIGN_PLAN.md`,
`specs/multi-gadget-workspaces/DESIGN_PLAN.md`, `specs/pi-ai-migration/DESIGN_PLAN.md`.

- Pick `<id>` yourself from the change; confirm it with the user in the same
  `AskUserQuestion` batch as the Phase 2 decisions rather than asking separately.
- If `specs/<id>/` already exists, you are amending an existing plan — read what's there and
  follow the amendment protocol below instead of starting a new document.
- One directory per feature. The `implementation-playbook` skill writes
  `specs/<id>/IMPL_PLAYBOOK.md` into the *same* directory, so a design plan and its playbook
  sit side by side. Anything else the work produces (feasibility notes, spikes, captured API
  research) belongs in that directory too.
- Create the directory if it doesn't exist. Don't invent a different root — not `docs/`, not
  `plans/`, not `.claude/`.

## When not to use this

- **One technology choice** with trade-offs (Kafka vs SQS) → that's an ADR.
- **User-facing behavior** of a feature → that's a product spec.
- **Already-settled change**, where the remaining work is mechanical translation → use the
  `implementation-playbook` skill instead. A design plan is often the *companion* to a
  playbook: this doc decides, the playbook executes. They share a `specs/<id>/` directory.
- **Greenfield.** This genre's power comes from precise knowledge of what exists today. With
  nothing to inventory, most sections collapse.

## Process

Do not start writing until Phase 3. A design plan written before the code has been read is
worthless in a specific, detectable way: it talks about "the storage layer" instead of
`nextGatekeeperId`.

### Phase 1 — Ground yourself in what exists

Read the actual code. Not the README, not the types file alone — the runtime.

Find and note, by exact name:

- The storage entities involved: table/collection names, singleton keys, ID counters, indexes
  (including uniqueness constraints, which are usually the thing that breaks).
- The public API surface that will move: interface names, method signatures, capability
  objects.
- The lifecycle chokepoints: constructors, entrypoints, restore/resume paths, migration gates.
  Enumerate *every* path into the component, not just the obvious one — the classic bug in
  this genre is a migration gated on `open()` when the thing also wakes via a hook delivery.
- Where persisted data can outlive a schema: logs replayed to reconstruct state, persisted
  callback params, blueprints/archives in object storage, records already in production.

Then write the **current state paragraph** (see template). One paragraph. If you cannot
compress it to one paragraph, you do not yet understand the system; if you can only write it
by being vague, you have not read enough. Show it to the user before going further — a wrong
current-state paragraph invalidates everything downstream, and it is cheap to correct here.

### Phase 2 — Surface and settle the decisions

Enumerate the genuinely open questions. A question is genuine if two competent engineers
would pick differently and the choice changes the diff. "Should we use a helper function?" is
not genuine; "are chat threads workspace-scoped or per-object?" is.

For each, work out the answer yourself first — including what you would reject and why — then
put the ones that are actually the user's call to `AskUserQuestion`, in batches of up to four,
with your recommendation first. Typical genuine questions in this genre:

- Scoping: which existing concept gains the new dimension, and which stays global?
- Identity/namespace: shared counter or separate namespaces? Are IDs ever reused?
- Storage shape: nested map vs named roots vs new collection — driven by what migration each
  forces.
- Compatibility obligations: what is deployed, what is only on a branch, what is in users'
  data. Branch-only things carry *no* obligation; say so explicitly and exploit it.
- Provisional/uncommitted state: if a change can be proposed and then accepted or reverted,
  does this new thing participate in that lifecycle?
- Deletion and orphans: what happens to things that lose their last reference?

Keep asking until nothing structural is unresolved. Every question you ask becomes a numbered
entry in the Resolved questions log, so nothing is wasted.

### Phase 3 — Write

Follow `reference/template.md` for the section order and the shape of each section, and
`reference/house-style.md` for voice. Both are short; read them before writing.

### Phase 4 — Review pass

Before handing it over, check the plan against `reference/house-style.md`'s checklist, and
specifically:

- Every decision that closed off an alternative names that alternative and why it lost.
- Every area section contains at least one explicit *negative* ("X does not change").
- The phasing produces commits a reviewer can actually review — schema/API first, behavior
  second, is the default cut.
- Persistent state has a migration story, and the migration is gated on every entrypoint.
- Anything replayed from a log has a replay-determinism story; anything created durably has a
  crash story.

## Amendment protocol

Design plans get reviewed and revised. Revise **in place, additively** — never silently
rewrite a decision, because the reasoning trail is half the document's value.

- New questions raised in review get appended to the log with a `(review)` marker:
  `**Q12 (review): ...?**`
- A decision that changes gets its superseded text struck through (`~~...~~`) with the
  revision below it, labeled with where it came from:
  `**Revised during phase 3 review:** the default gadget's top-level flatten was dropped ...`
  and the reason it changed.
- A whole new workstream added after the fact becomes `# Part 2: <name>`, with its own core
  model / phasing / resolved-decisions sections, and an opening note stating what
  compatibility obligations Part 1 does or does not carry into it.
- When a Part 2 addresses something Part 1 listed as a follow-up, annotate the Part 1 bullet
  *(Now addressed by Part 2 below.)* rather than deleting it.

## Reference files

- `reference/template.md` — section-by-section skeleton with the shape and failure mode of
  each section.
- `reference/house-style.md` — voice rules and the pre-delivery checklist.
