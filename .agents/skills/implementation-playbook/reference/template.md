# Implementation playbook template

Nine numbered sections plus a preamble. Numbering them is deliberate: the doc gets referenced
by section ("Phase 2 items are follow-ups (§8)") from inside itself and from commit messages.

---

## Title + preamble

Written to `specs/<id>/IMPL_PLAYBOOK.md`.

```
# <From X> to <Y>: implementation playbook

*Companion to `specs/<id>/DESIGN_PLAN.md`. This is a direct implementation guide for a coding
agent: all design decisions are made; implement the whole thing in one pass. The project is in
early alpha — minor behavioral regressions are acceptable and will be fixed as discovered. Do
not build parallel/intermediate implementations, compatibility shims for the old path, or
elaborate test harnesses.*
```

Italicized. Four jobs, in this order:

1. **Provenance** — what decided this, with a path. Normally
   `specs/<id>/DESIGN_PLAN.md`; name whatever the real source is (feasibility report, design
   review thread) and put it in the same `specs/<id>/` directory if it isn't already
   somewhere durable.
2. **Audience and mode** — a coding agent; one pass; decisions are made.
3. **Regression tolerance** — the calibration that governs every judgment call downstream.
4. **Forbidden shapes** — the specific defensive constructions the implementer must not build.

Then a **Reference materials** paragraph: where authoritative source lives (exact
`node_modules/**/dist/**`-style paths), why it's usable (unminified, doc comments preserved,
`.d.ts` for signatures, source maps if the TS is ever needed), an instruction to read it
directly, and a note that the appendix is a snapshot while the installed source is
authoritative.

---

## `## 1. Fixed decisions`

Opens: *These are settled; do not re-litigate during implementation.*

Numbered. Each item is one decision, terse, with the reasoning compressed to a clause and the
consequences spelled out. Include:

- **Scope** — what phase this is, and one line pointing at what's deferred (`§8`).
- The core structural choices, each naming the specific API/approach chosen over the
  alternative: "the low-level `runAgentLoopContinue` (awaited event sink), not the stateful
  `Agent` class."
- **Deletions**, stated as such: "delete today's `prepareStep` strategy. Assume pi does it
  right."
- **Trust statements** where you are deliberately not reimplementing something: "No custom
  breakpoint logic of any kind."
- **Pre-authorizations** the implementer would otherwise stall on: "`nodejs_compat`:
  pre-authorized — if the library needs it, add it to both wrangler configs."
- **Testing policy** — what must pass, what must be rewritten, what must not be built.
- **Commit shape** — "One commit (or one small stack if `git add -p` hygiene demands). No
  requirement that intermediate states build."

10–15 items is typical. Anything that would make the implementer pause and weigh options
belongs here.

**Failure mode:** a decision phrased as a preference. "Prefer the low-level loop" invites a
different choice; "use the low-level loop, not the `Agent` class" doesn't.

---

## `## 2. Invariants — do not change`

Bulleted. The things that must come out the other side untouched, at the finest granularity
you can state:

- Whole packages with **zero diff** — say "zero diff" explicitly.
- Named types and their fields.
- Named lifecycle logic, described precisely enough to be recognized:
  "The Overseer turn lifecycle: `startAgent`, `#runAgentTurnWithContext`'s loop/nudge/callback
  logic, compaction commit/rollback. **Only** its error-classification `catch` and the
  `getModel` call change."
- Files that survive and why they weren't replaced by the new library's equivalent — this one
  matters, because an implementer will otherwise "helpfully" swap them.
- "Frontend: zero changes."

The `Only X changes` construction is the workhorse here. Use it wherever a mostly-invariant
thing has a small hole punched in it.

---

## `## 3. Dependencies and configuration`

- **Remove:** exact package names, with any verification note ("verify `zod` has no remaining
  uses after the tool rewrite").
- **Add / already added:** exact names and version pins; note expected install-time noise so
  it isn't chased ("pnpm reported ignored build scripts — expected; do not `approve-builds`
  unless something actually breaks at runtime").
- **Import discipline:** which entry points are allowed and which are forbidden, with reasons.
  "Never `providers/all` (~30 providers in the bundle), never `/compat` (side effects)."
- **Environment variables:** each one that becomes required, optional, or newly conflicting,
  plus every place the change ripples — type declarations, README, dev-server scripts,
  config generators. State the enforcement point ("make the constructor throw when absent").
- **Build/bundle checks:** anything that might not bundle, and the escape hatch if it doesn't
  ("mark it external or alias it out").

---

## `## 4. Implementation guide`

The body. Opens with the line-reference caveat:

> (`file.ts:NNN` references are approximate — recent changes shifted some line numbers; locate
> by symbol name.)

Then `### 4.N <filename> — <verb>` per file, ordered so that files defining shared shapes come
first. Verbs: *new, small* / *rewritten* / *the core rewrite* / *types only*.

Within each:

- **Target signatures in fenced code blocks.** Give the exported type or function the
  implementer must produce, with comments annotating each field's purpose and what it
  replaces:
  ```ts
  aiGatewayLogRoute?: AiGatewayLogRoute;  // for cost accounting; replaces
                                          // getModelWithLogRoute's second return
  ```
- **Old→new mapping tables** whenever more than three shapes translate. Two columns, headed
  `Today | <new>` or `<old event> | action`. This is the single most useful device in the
  genre — it makes a mechanical rewrite mechanical.
- **Implementation facts you verified**, as a bulleted "Key implementation facts" list, so the
  implementer doesn't re-derive them.
- **Explicit anti-instructions** for the appealing-but-wrong path: "Don't use the library's
  `Models` collection or credential store — build the objects ourselves."
- **Seams for known future work**, marked but not built: "leave a clearly marked seam — the
  place where a custom `fetch` would be injected — with a comment referencing the upstream ask.
  Don't implement the shim."
- **Signature changes** stated as a before/after arrow:
  `runAgent(hooks, chosenModel: LanguageModel, ..., aiGatewayLogRoute?)` →
  `runAgent(hooks, handle: ModelHandle, ...)`.
- **Uncertainty with a fallback**, inline, parenthesized: "(Exact signature per source —
  verify. If it rejects our context shape, fall back to X.)"
- **A forced choice when two approaches both work**: describe both, then "Choose one approach
  and apply it uniformly."

---

## `## 5. What "reasonable" means for the judgment calls`

Short bulleted list covering every place exact parity is impossible or not worth it. Each
bullet: the situation, then the rule.

Typical entries: user-visible error text, retry behavior, usage/cost accounting, ordering
differences between the old and new libraries, anything whose old form was an artifact of the
old library rather than a requirement.

The standing instruction: *match today's behavior in spirit, not in detail — do the most
reasonable thing.* And for ordering: *if the library's actual behavior differs from this
document's description, trust the source and preserve our semantics.*

---

## `## 6. Things that look tempting but are out of scope`

One paragraph or a single bulleted line-up of named temptations. Not generic scope control —
the *specific* things this implementer, doing this change, will want to do: the adjacent
refactor, the file that now looks redundant, the tuning knob newly exposed, the abstraction
that would unify two things.

Naming them is what makes them not happen.

---

## `## 7. Verification`

Numbered, bounded, three tiers:

1. **Commands that must pass** — `pnpm build && pnpm lint && pnpm test` — including which test
   files must be rewritten and which must pass untouched.
2. **Manual smoke checklist** — a nested list of concrete user-level flows, each specific
   enough to be performed without interpretation: "abort mid-stream (cancel button); restart
   dev server mid-turn and confirm resume"; "a chat from an existing dev database
   (pre-migration log) replays without errors."
3. **Sanity checks** for things that could silently no-op, flagged as such: "Confirm cache
   hits happen at all (`cacheRead > 0` on a second turn) — a sanity check, not a tuning
   exercise."

State the ceiling explicitly: no parity harness, no new automated suites.

---

## `## 8. Follow-up work (separate commits/PRs, after Phase 1 lands)`

Numbered. Each entry: what it is, how it would be done, and — where relevant — the upstream
change that would make it unnecessary. Include a sub-list of **upstream asks to file now**,
since those have lead time and are cheap to file while the context is fresh.

---

## `## 9. Appendix: verified API reference`

Opens: *Verified against the source; re-verify signatures before relying on them.*

Organized by module, each with its source path. Compressed reference notes, not prose: type
shapes on one line each, behavioral facts as short bullets, and — critically — the **negative
facts** you had to check ("`options.metadata` is **not** sent as `cf-aig-metadata`"; "zero
binding awareness"; "errors are events + final messages, never thrown").

Negative facts are why this section exists. They're the ones an implementer would otherwise
assume wrongly and only discover at runtime.
