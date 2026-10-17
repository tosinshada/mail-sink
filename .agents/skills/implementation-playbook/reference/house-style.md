# House style

## Voice

**Imperative and settled.** "Replace the provider stack." "Delete the middleware." "Keep
`streaming-json-parser.ts`." Never "we could", "consider", "it may be preferable to".

**Address the implementer's temptations directly.** Much of the document is negative
instruction, and that's correct: "Don't use the library's `Models` collection." "Don't route
through the library's `onUpdate`." "Don't chase exact message-format parity." "Don't implement
the shim." Each of these prevents a specific plausible wrong turn.

**Cite source paths, not memory.** When a behavioral claim matters, say where it was read:
"check `node_modules/@pkg/dist/api/openai-responses.js` and set whatever keeps requests
stateless." This both grounds the claim and tells the implementer how to check it themselves
when your snapshot is stale.

**Mark uncertainty inline, with a fallback.** Never smooth over a gap:

> (Exact signature/order per source — verify. If `runAgentLoopContinue` rejects our context
> shape — it requires the last message to be user/toolResult, which replay guarantees — fall
> back to `agentLoop` with the tail split out.)

**Calibrate to the project's regression tolerance, repeatedly.** In an alpha project, say so
at the points where it licenses a shortcut: "if the events turn out to be delayed, live with
it (alpha) and note it." This is what stops the implementer gold-plating.

**Terse over complete.** Sentence fragments are fine in tables and bullet lists. "Ollama:
baseUrl = `config.apiUrl` + `/v1`, bearer header only when `apiToken` non-empty (as today)."

**"as today" / "unchanged" are load-bearing.** Use them liberally to mark the boundary of the
change; they save the implementer a diff-check every time.

## Formatting conventions

- Sections numbered `## N.` and cross-referenced as `§N` from inside the document.
- Italic preamble, before section 1.
- Fenced `ts` blocks for signatures the implementer will reproduce; annotate fields with
  trailing comments saying what they replace.
- Two-column tables for old→new shape mapping and for event→action dispatch.
- Bold for the object of an instruction: "**Imports:** drop everything from `"ai"`".
- Backticks on every identifier, path, package, env var, and file name.
- `file.ts:NNN` refs are fine and expected — but declare once, up front, that they're
  approximate and to locate by symbol.

## Banned

- Alternatives presented for the implementer to weigh. If two options exist, pick one, or say
  "choose one and apply it uniformly."
- Compatibility shims, adapter layers, feature flags, and parallel old/new paths — unless
  explicitly required. Say this in the preamble and again in the out-of-scope list.
- Invented test infrastructure. State the test bar; forbid exceeding it.
- Recaps and summary sections. The document is the reference; it doesn't summarize itself.
- Estimates, timelines, risk ratings.
- Claims about a library's behavior that you did not read the source for.

## Pre-delivery checklist

1. The file is at `specs/<id>/IMPL_PLAYBOOK.md`, reusing the `<id>` of an existing
   `specs/<id>/DESIGN_PLAN.md` if there is one.
2. Preamble names the source of the decisions (by path), the audience, the regression
   tolerance, and the forbidden constructions.
3. Reference-materials paragraph gives real paths to authoritative source and says the
   appendix is a snapshot.
4. Fixed decisions include: scope, commit shape, testing policy, and every pre-authorization
   the implementer would otherwise stall on.
5. Every piece of code that must be deleted is named. (Walk the old implementation and check.)
6. Invariants include at least one "zero diff" claim, and every partially-invariant item uses
   an "only X changes" clause.
7. Every file in the change has a `### 4.N` subsection.
8. Every set of more than three translated shapes has a mapping table.
9. Every unverified claim carries "(verify)" and a fallback.
10. The out-of-scope list names *specific* temptations from this change, not generic ones.
11. Verification states both what must pass and that nothing beyond it is required.
12. The appendix records the negative facts — what the library does *not* do.
13. Reread as the implementer: is there anywhere I'd have to make a decision, build a
    fallback, or invent scope? Each hit is a fix, not a note.
