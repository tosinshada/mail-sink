# House style

## Voice

**Assert, then justify.** Every decision is written as a completed fact. "Chats are
workspace-scoped." Not "we propose that chats be workspace-scoped" or "chats could be
workspace-scoped." The justification follows the assertion; it never replaces it.

**Concrete over categorical.** Name the identifier. `nextGatekeeperId`, not "the ID counter";
`overseer.ts:1036-1078`, not "the filtering logic". Line refs are welcome and are understood
to be approximate — pair them with a symbol name so they survive drift.

**Compress ruthlessly.** These documents are dense on purpose. Semicolons and em-dashes doing
structural work is correct here. A sentence that survives deletion should be deleted.

**Parenthetical rationale.** The reason a thing is the way it is often belongs inside the
sentence stating it: "The counter keeps its `nextGatekeeperId` storage name (renaming would
require a storage migration for no functional gain)."

**Name the trade you are making, out loud.** The vocabulary is specific and reusable:
"documented wart", "acceptable temporary regression while in alpha", "accepted asymmetry",
"annoying but unavoidable", "compatibility deliberately broken", "Resolved: punted". Using
these labels is how the plan proves it saw the cost rather than missed it.

**Rejected alternatives are content.** Every closed door gets a sentence. The strongest form
gives a mechanical defeat condition rather than a preference:

> A `defaultBindings` storage singleton was considered and rejected: every write path
> (creation, merge promotion, rename, unbind, deletion) would have needed an
> insert/propagate/drop hook to keep a second copy of derivable state consistent.

**Consequences get their own sentence.** "Consequence: reverse lookups ('which gadgets bind
gatekeeper X') become a scan over the `gadgets` collection. Fine, since gadget counts are
small." State the cost, then dispose of it.

**Bound the scope in the text, not just in a list.** When a section could sprawl, say where it
stops: "Targets that are other gadgets or other spawners are out of scope for v1 (export fails
with a clear error)."

## Formatting conventions

- `**Bold lead-in.**` opens each numbered decision and each resolved-question answer.
- `→ **Resolved:**` prefixes every answer in the questions log.
- `~~strikethrough~~` for superseded text; never delete a revised decision.
- Backticks for every identifier, filename, storage key, env var, and type.
- Tables only for old→new mappings of many small items; prose and bullets for everything else.
- Inline type sketches instead of full code blocks:
  `BindingRecord = {target: WorkpieceId, blueprintAnnotation?: BlueprintBindingAnnotation}`.
  Reserve fenced blocks for signatures the implementer will copy.

## Banned

- Estimates, story points, timelines, staffing.
- "We should consider", "it might be worth", "possibly", "TBD" outside the follow-up list.
- Restating a decision in a summary that adds nothing.
- Sections that exist to look thorough: risk matrices, success metrics, stakeholder tables.
- Abstraction where a name exists. "the persistence layer" when you mean `ctx.storage`.
- Politeness scaffolding: "As mentioned above", "It's important to note that".

## Pre-delivery checklist

Run this before handing the document over.

1. The file is at `specs/<id>/DESIGN_PLAN.md` with a kebab-case `<id>` matching the title, and
   any companion playbook shares the directory.
2. **Current state** is one paragraph and names at least five real identifiers.
3. Every core decision states an alternative it beat, with a reason.
4. Every accepted downside is labeled as such somewhere in the doc.
5. Each area section contains at least one explicit "does not change" statement.
6. Every question you asked the user during Phase 2 appears in the Resolved questions log.
7. Persisted state that changes shape has: a migration, a trigger for it, and a statement of
   which entrypoints it is gated on.
8. Anything reconstructed by replaying a log has a determinism story; anything created durably
   mid-operation has a crash story ("what if the process dies here?").
9. Deletion is answered: what happens to references to a deleted thing, and to things that
   lose their last reference.
10. Phase 1 is schema/API only, and at least one phase boundary is marked as a review
   checkpoint.
11. The `NOT implemented` list exists and each entry says what it would take.
12. No estimates. No hedging verbs outside the follow-up list.
