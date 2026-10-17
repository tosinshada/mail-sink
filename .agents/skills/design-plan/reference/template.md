# Design plan template

Section order is fixed. Sections may be short but should not be omitted; if one genuinely
doesn't apply, say so in a line rather than dropping the heading.

---

## `# <Feature or change>: Analysis & Plan`

Written to `specs/<id>/DESIGN_PLAN.md`.

Title names the *change*, not the ticket. "Multi-Gadget Workspaces", "Chat-scoped bindings",
"Moving inference off the SDK". The `<id>` is the kebab-case form of the same name
(`multi-gadget-workspaces`, `chat-scoped-bindings`), so title and path stay legible together.

---

## `## Current state (one paragraph)`

One paragraph. Dense, comma-and-semicolon heavy, entirely concrete.

It must name real identifiers: table and collection names, singleton storage keys, ID
counters, index names, class names, the hard-coded string constants that encode the current
assumption. The goal is that a reader who knows the codebase nods, and a reader who doesn't
now knows exactly which files to open.

Good shape:

> Today "workspace" = "gadget" = one Overseer DO. The DO holds: one code history
> (`code`/`snapshots` collections replaying into a Y.Doc whose single unnamed root map is
> filename→Y.Text), one `gatekeepers` table (sequential IDs from `nextGatekeeperId`, with
> `bindingName` stored *on* the gatekeeper record + unique `byBindingName` index), one
> hard-coded `"gadget"` facet, chats/actions/hooks/sharing/observers all keyed without a
> gadget dimension, and loopback props that identify targets by `overseerId` alone.

**Failure mode:** abstraction. "The system currently supports a single workspace with an
associated document store" says nothing and hides the fact that nobody read the code.

---

## `## Core architectural decisions`

Numbered. Each decision opens with a **bolded assertion sentence** — the decision itself,
stated flatly — then unpacks.

```
**3. Bindings: move binding names off `GatekeeperRecord` into the gadget record, as
binding-edge records.** <one or two sentences of mechanism> ... <parenthetical recording the
alternative that lost, and why>. The `bindingName` field and unique `byBindingName` index on
`gatekeepers` (overseer.ts:508-515) go away.
- Consequence: <what gets worse, and why that's acceptable>.
- Policy scope (resolved): <the sub-decisions this forces>.
```

Each decision should carry, in whatever order reads best:

- **Mechanics** — the concrete shape. Type sketches inline are welcome:
  `BindingRecord = {target: WorkpieceId, blueprintAnnotation?: BlueprintBindingAnnotation}`.
- **Reasons to prefer it** — as a bulleted list when there are several, each one a distinct
  argument, not a restatement.
- **The rejected alternative and why it lost.** Non-optional. Prefer concrete defeat
  conditions ("Y types can't move once integrated — you'd clone content, and you'd still need
  a new root name, so it buys nothing") over taste ("cleaner").
- **Consequences you are accepting**, labeled: "documented wart", "acceptable temporary
  regression while in alpha", "accepted asymmetry", "annoying but unavoidable".
- **Future payoff**, when the shape was chosen partly for it — one line, clearly marked as
  not-this-change.

Aim for 5–10 decisions. Fewer means you're under-deciding; more means some are really
change-inventory items.

---

## `## Change inventory by area`

One `###` subsection per package, module, or subsystem — organized by *where the code lives*,
not by task. Typical set: shared API/types, the core runtime, each major subsystem, storage
schema, frontend, migration.

Within each: bullets of deltas. Rules:

- Reference symbols and `file.ts:NNN` line ranges when pointing at existing code.
- State explicit **negatives**: "`ActionRecord` does **not** gain a `gadgetId`. Instead, ...",
  "Chat storage needs no re-keying.", "the blueprint archive format stays compatible." These
  are the highest-value lines in the section — they pre-empt the implementer's wrong guess.
- Where a name or shape changes, give both sides.
- Where something is knowingly deferred inside an otherwise-changed area, mark it inline with
  the reason and a pointer to the follow-up list.
- Include a `### Migration` subsection whenever persisted state changes: what triggers it,
  where it is gated, exactly what it rewrites, and what it deliberately leaves alone.

**Failure mode:** turning into a task list. "Update the tests" is a task; "the `gatekeepers`
table loses `bindingName` and its `byBindingName` index" is an inventory item.

---

## `## Resolved questions`

A numbered append-only log. Format:

```
**Q4: Workspace title vs gadget titles.**
→ **Resolved:** keep auto-titling the *workspace* from the first chat (overseer.ts:3114).
Gadget titles need no default — `createGadget` always specifies a title — and can be renamed
by the user in the sidebar. The migrated legacy gadget inherits the workspace title.
```

- Questions surfaced during a later review round are marked in the heading:
  `**Q13 (review): Which gadget owns persistent callbacks minted inside `executeCode`?**`
- "Resolved: punted" is a legitimate answer when paired with what happens in the meantime.
- Long answers may take sub-bullets; a genuinely complicated one (crash-safety, provisional
  state) can run half a page and should.
- When an answer is later revised, keep the original and strike it: `~~old text~~` followed by
  `**Revised during phase N review:** ...` and the reason.

This section is the plan's memory. Reviewers who ask "did you think about..." get answered by
a link, not a rerun of the discussion.

---

## `## Phasing`

Numbered list; each entry is one commit (or one reviewable PR). Rules:

- **Cut schema/API first, behavior second.** The default first phase is "API and storage
  schema changes only — no logic implemented yet," which gives the reviewer a small, total
  view of the shape before any of it moves.
- Mark explicit stopping points: **Review checkpoint before proceeding further.**
- State for each phase what is *observably* true at its end — especially "the product is fully
  functional again, but there is no visible difference yet for either users or agents."
- Name transitional scaffolding and when it dies: "Workspace creation still auto-creates the
  initial gadget, since the agent can't create gadgets yet."

Then, immediately after, a bulleted:

`NOT implemented (left for follow-up changes):`

Every deferral, each one a sentence explaining what it would take. This list is how a plan
stays finite. When a later Part addresses one, annotate it *(Now addressed by Part 2 below.)*
rather than deleting the line.

---

## `## Resolved decisions` (Part 2+ only)

When a plan grows a `# Part 2:` section after the first is complete, that part gets a compact
numbered decisions list at its end summarizing its own settled choices — a digest, since by
then the reasoning is spread across the part's prose.

---

## Optional sections, when earned

- `## Name validation` / `## Migration` / `## Env structure` — a decision complex enough to
  need its own top-level treatment gets promoted out of the decisions list. Do this when the
  decision has its own sub-decisions and edge cases, not merely because it's important.
- `# Part 2: <name>` — a workstream added after Part 1 was written. Opens with a note on
  provenance and compatibility: "This part was added after everything above was completed (but
  before any of it was merged or deployed — so nothing introduced in Part 1 carries a
  compatibility obligation)."
