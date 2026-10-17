---
name: business-logic-blueprint
description: Review an existing implementation and write a blueprint of its business logic — a high-level requirement list, a language-agnostic technical design (invariants, data model, state machines, flows, calculation rules, concurrency and integration contracts), and a decision sheet of every point where the target should differ from the source, agreed with the human before it is recorded as settled. Use this whenever the user wants to reverse-engineer, document, extract, characterize or "blueprint" what a service, module or feature actually does; when they are about to migrate, rewrite, port or consolidate a system and need the behavior captured first; or when they say "blueprint", "map out the business logic", "what does this service really do", "write up the rules in X so we can rebuild it", "review this implementation and produce a spec". Writes `<SUBJECT>_BLUEPRINT.md` in the reviewed app's docs folder. Do NOT use for a change whose design is open but whose current behavior is already understood (use design-plan), for a decided change that only needs translating into steps (use implementation-playbook), or for a code-quality review of a diff (use code-review).
---

# Business logic blueprint

## What this produces

One markdown document that lets someone rebuild a system's behavior without reading its
code — in any language, on any stack. It has three layers, and each answers a different
reader's question:

1. **Requirements** — *what must the system do?* A short, numbered, high-level list a
   product owner can read and challenge.
2. **Technical design** — *what rules make that true?* Invariants, entities and their
   constraints, state machines, step-by-step flows, calculation rules, concurrency and
   idempotency, integration contracts. Language-agnostic.
3. **Decision sheet** — *where does the target differ from what exists, and who agreed?*
   Every preserved behavior, corrected defect, and changed policy, with its status and the
   evidence behind it.

The third layer is what separates a blueprint from documentation. Existing code mixes
intended behavior, accidents, and bugs, and it cannot tell you which is which. The blueprint's
job is to sort them — and the sorting that changes behavior is the human's call, not yours.

Two worked examples of the finished genre live in this repository. Skim their structure (not
their domain) before writing your first one:

- `Apps/Kanda.CoreBankingApi/docs/LEDGER_WALLET_PAYOUT_RECONCILIATION_BLUEPRINT.md` — a
  build-from-scratch blueprint: glossary, fixed decisions, ID'd invariants, algorithms.
- `Apps/Kanda.CoreBankingApi/docs/LOAN_SERVICE_MIGRATION_BLUEPRINT.md` — a
  review-and-migrate blueprint: source inventory, preserve/correct classification, a dated
  decision sheet with provenance. This is the closer model for most uses of this skill.

Both are stack-specific in places (they name C# types and EF Core). That is the one thing
*not* to copy — see "Language-agnostic" below.

## Output location

Write to `docs/<SUBJECT>_BLUEPRINT.md` inside the root of the app or service being reviewed
(for example `Apps/Kanda.CoreBankingApi/docs/`), with `<SUBJECT>` in upper snake case naming
the capability, not the ticket. If the repo has no such docs folder, use the nearest existing
documentation directory. State the path you chose when you show the inventory in Phase 1 so the
user can redirect it cheaply.

If the file already exists, you are amending — follow the amendment protocol at the end.

## Process

The order matters: read, then classify, then agree, then write. A blueprint drafted before the
decisions are agreed has to be rewritten; one drafted before the code is read is fiction.

### Phase 1 — Fix the scope and inventory the source

Pin down what is being reviewed: which service/module/feature, which entrypoints count, and
what the blueprint is *for* (rebuild in place, migrate elsewhere, port to another stack, or
just capture). The purpose decides how much target design belongs in it.

Then inventory every way into the behavior. Missing an entrypoint is the classic failure,
because the rule you didn't find is usually enforced on only one path:

- inbound interfaces: API actions, message/event handlers, webhooks, CLI/admin commands;
- background work: scheduled jobs, workers, workflows, retries, reconcilers;
- outbound calls: other services, providers, storage, queues;
- persisted state: entities, constraints, indexes, seed/reference data, configuration.

For a large surface, count it (for example "136 actions in 18 controllers") and group it into
capability families so coverage is checkable. Show the user the scope, the family list, and the
output path before going deeper — a wrong scope invalidates everything after it.

### Phase 2 — Extract the behavior, with evidence

Read the code that runs, not the code that describes. For each capability family record the
rule and where it is enforced, citing file and symbol.

Evidence discipline — these are the ways extraction quietly goes wrong:

- **Trace the executing path.** A validator, a default, or a guard only counts if the command
  path reaches it. Entity property initializers are overridden by command paths and
  configuration; work out the real precedence and record the *resolved* value.
- **Comments, DTO docs and names are claims, not behavior.** When a comment and the code
  disagree, the code is the behavior and the disagreement is a finding.
- **Tests are evidence of intent, fixtures are not evidence of production values.** A rate in a
  test builder is not deployed pricing.
- **Presence is not deployment.** Code in the tree does not prove it runs, is enabled, or is
  reachable. Say what you inspected and what you could not establish.
- **Compare paths that should agree.** Preview versus execution, validator versus handler,
  happy path versus retry path, API versus background job. Divergence between them is where
  defects live.
- **Follow delegation to the end.** If the service hands money, state, or authority to another
  service, the rule lives there; read it.
- **Record absences.** "The source has no cap / no maximum age / no cancellation capability"
  is a finding. Unrecorded absences get filled in later with invented guarantees.

Capture concrete numbers as you go (thresholds, batch sizes, timeouts, rounding rules, state
names). They become the worked examples and the defaults table.

### Phase 3 — Classify every finding

Sort what you found. The classification is the backbone of the decision sheet, and it decides
who gets to settle each item:

| Class | Meaning | Who settles it |
|---|---|---|
| **Preserve** | Intended behavior, carried forward as is. | You; record the source evidence. |
| **Engineering control** | Target mechanics that change *how*, not *what* (atomicity, idempotency, locking, immutability, bounded work). No change to business outcomes. | You; state it changes no economics or policy. |
| **Defect correction** | Source behavior that contradicts its own intent or another path. | Human confirms it is a defect, not a feature someone relies on. |
| **Policy change** | The target should behave differently for the business: amounts, eligibility, timing, ordering, who is authorized. | Human decides. |
| **Gap** | The source is silent or ambiguous and the target needs an answer (a default, a limit, an unhandled case). | Human decides, or explicitly authorizes you to pick a labelled fallback. |
| **Out of scope / deferred** | Exists or is wanted, but not part of this blueprint. | Human confirms the boundary. |

When unsure between *engineering control* and *policy change*, ask: would an end user, a
counterparty, or the books see a different outcome? If yes, it is policy. Erring toward asking
is cheap; silently reclassifying a business change as "engineering" is how a rewrite ships
different numbers nobody approved.

### Phase 4 — Agree the changes with the human

Everything in the last four classes goes to the human before it is written as settled. This is
the point of the skill, so do it properly:

- Work out your own recommendation first, including what you would reject and why.
- Ask with `AskUserQuestion`, in batches of up to four, recommendation first. Each question
  states the source behavior with its evidence, the proposed target behavior, and what
  concretely differs — ideally with one small numeric example.
- Lead with the questions that reshape the design (ownership, accounting or data model,
  scope boundary). Answers there can dissolve later questions.
- Keep going until nothing that changes behavior is unresolved. If the user defers an item,
  that is a resolution: record it as deferred, with the baseline that applies meanwhile.
- If the user is unavailable or tells you to proceed on your judgment, write your
  recommendation as **Proposed** — never as Confirmed.

Record every outcome with an honest status. The statuses mean different things and a reader
will act on them:

- **Confirmed** *(date)* — the human explicitly agreed, in this review.
- **Carried forward** — settled in an earlier document; not re-approved here.
- **Inferred** — derived from source code or configuration; nobody has approved it as policy.
- **Chosen fallback** — you picked a value because the source had none, under explicit
  authorization to do so.
- **Proposed** — your recommendation, awaiting agreement.
- **Deferred** — consciously left open; names the interim baseline.

Do not launder an inference into a confirmation. "The code does X" is not "the business wants
X", and the document must let a later reader tell them apart.

### Phase 5 — Write

Follow `reference/template.md` for section order and the shape of each section. Scale it to the
subject: a small feature needs the header, requirements, invariants, flows and decision sheet;
drop sections that would be empty rather than padding them.

Writing rules that make the document usable:

- **IDs on everything a test or a conversation will cite.** Requirements `R1…`, decisions
  `D1…`, invariants `INV-<area><n>`. Later sections and tests refer to the ID.
- **One word for one idea.** Put the vocabulary in a glossary and hold to it; synonyms read
  as different concepts to an implementer.
- **Tables for anything enumerable** — inventories, decisions, invariants, state matrices,
  uniqueness constraints, defaults. Prose for reasoning and for the *why* behind a rule.
- **Numbered steps for flows**, in the order things must happen, including what is checked
  under which lock and what commits together.
- **State the negative.** "No journal on hold placement", "a timeout is not a failure", "X is
  not inferred from the source". Explicit non-behaviors stop an implementer filling a gap with
  the nearest plausible thing.
- **Worked examples with real numbers** for every calculation and every corrected defect, so a
  numerical difference can be recognized as intended rather than as a regression.
- **Do not claim what was not done.** A blueprint is design input. It does not assert that
  anything is implemented, deployed, or verified unless you verified it.

#### Language-agnostic

The technical design must survive a change of language and framework, because the usual reason
to write a blueprint is that the implementation is about to change.

- Describe types by meaning: "signed 64-bit integer of minor units", "calendar date", "UTC
  instant", "opaque prefixed sortable id" — not `long`, `DateOnly`, or a library type.
- Write algorithms as numbered steps or neutral pseudocode. No language syntax, generics,
  async keywords, attributes, or ORM calls.
- Name entities, fields, states and operations in domain terms. Interface contracts list
  operations, inputs, outputs and errors — not classes or method signatures.
- Where correctness depends on a platform capability (row locks, unique partial indexes,
  transactional outbox, durable timers), state the *required primitive* and collect them in
  one appendix, so a port knows what it must supply.
- Source citations are the exception: they point at real files and symbols in whatever
  language the source uses. Keep them in the inventory, the decision sheet and the reference
  appendix — as evidence, never as design.

If the user wants stack-specific guidance as well, put it in a clearly separated section or
appendix so the agnostic core stays intact.

### Phase 6 — Review pass

Before handing it over, check:

- Every requirement traces to source evidence or to a decision; every decision-sheet row has a
  status and an obligation the implementer must meet.
- Every inventoried family has a disposition (preserve, change, retire, defer). Nothing is
  retired by omission.
- Nothing marked Confirmed lacks an explicit human agreement in this conversation.
- Every defect correction and policy change has a worked example or acceptance case.
- Each invariant is testable as written, and no flow violates one.
- Search the design sections for language or framework names and syntax; move or reword them.
- The out-of-scope list exists and says unsupported cases fail explicitly rather than mapping
  to the nearest supported one.

Finish by telling the user where the file is, how many items sit in each status, and which
**Proposed** or **Deferred** items still need them.

## Amendment protocol

Blueprints are revised as decisions land. Revise in place and keep the trail — the history of
*why* is half the value.

- A newly agreed decision changes its row's status and gains the date; add a dated note to the
  header status block summarizing what was accepted.
- A reversed decision keeps its old text, marked superseded with the date and reason, beside
  the new resolution.
- New source findings are added to the inventory and classified like any other; do not edit an
  existing Confirmed row to absorb them.
- Scope moved in or out is recorded in the scope section as a transfer, with what it does and
  does not imply (a scope transfer is not implementation or approval of new policy).

## Reference files

- `reference/template.md` — section-by-section skeleton with the shape and common failure of
  each section. Read it before Phase 5.
