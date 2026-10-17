# Blueprint template

Section order for `<SUBJECT>_BLUEPRINT.md`. Keep the order; drop sections that do not apply
(the note under each heading says when). Sections marked **core** appear in every blueprint.

Angle brackets are placeholders. Example rows show shape only — replace them.

---

````markdown
# <Subject> Blueprint

> **Audience:** <who implements from this, who reviews it>
>
> **Status:** <draft | reviewed design blueprint>, <date>. <One or two sentences on what is
> settled and what is not: which decisions are Confirmed, which are Inferred or Proposed.>
> <Dated one-line notes for each later acceptance: "**Policy update:** … accepted on <date>.">
>
> **Source reviewed:** <service/module paths>, <branch or commit>, <date>. Presence of code or
> tests is not evidence of deployment or completed verification.
>
> **Scope:** <one paragraph: what this covers and the purpose — capture, rebuild, migrate, port>

## Contents
<numbered list linking each section>
````

## 0. Purpose, scope, and precedence — core

- What is being captured and why, in one paragraph.
- **In scope** / **Out of scope** lists. End out-of-scope with the rule that unsupported cases
  fail explicitly rather than falling through to the nearest supported behavior.
- **Precedence:** what controls style (the host project's conventions file) versus what this
  document controls (business behavior and invariants), and what to do on conflict (the
  invariant wins; stop and report).
- Optional: reading order for an implementer, and what "done" means for a feature.

*Failure mode:* scope stated as a topic ("loans") instead of a boundary (which operations,
which actors, which data).

## 1. Glossary

````markdown
| Term | Meaning |
|---|---|
| **<Term>** | <One meaning. Note what it is *not* if it is easily confused.> |
````

Include when the domain has more than a handful of terms or the source uses one word for two
things. Use each term with exactly this meaning everywhere after.

## 2. Current implementation inventory — core

What exists, with evidence, and what happens to each piece.

````markdown
| Capability / source | Current observation | Target disposition |
|---|---|---|
| `<path>` `<Symbol>` | <What it actually does; data shapes; which paths call it.> | <Preserve / change / retire / defer, one line.> |
````

Add separate small tables where they apply:

- **Entrypoint families** — interface family, action count, operations, disposition.
- **Background work** — job/worker, cadence, what it does, replacement owner.
- **External integrations** — counterparty, operations used, what the source does on
  timeout/unknown outcome, capabilities the source does *not* have.

*Failure mode:* listing files without observations; omitting jobs and callbacks because they
are not in the API.

## 3. Requirements — core

High level, numbered, readable by a non-engineer. One capability or obligation per row; ten to
thirty rows for most subjects. Detail belongs in the design sections, linked by `§`.

````markdown
| ID | Requirement | Basis | Design |
|---|---|---|---|
| R1 | <The system must … (outcome, not mechanism)> | <Preserve — `<source>` / D4 / Confirmed <date>> | §9.2 |
````

Group under short subheadings (by actor or capability) if there are more than about fifteen.

*Failure mode:* requirements that restate implementation ("calls the X service") or that have
no basis column, so nobody can tell an observed behavior from a new wish.

## 4. Fixed decisions — core

The settled choices an implementer must not reopen. Only Confirmed and Carried-forward items
belong here; Proposed items stay in the decision sheet until agreed.

````markdown
| ID | Decision | Reason / consequence |
|---|---|---|
| D1 | <The choice, stated so it can be violated.> | <Why; what it rules out.> |
````

Follow with a short "out of scope unless a human adds it" list if not already in §0.

## 5. Invariants — core

Statements that must hold after every committed operation. Each is testable and has an ID that
tests cite.

````markdown
| ID | Invariant |
|---|---|
| INV-<A>1 | <Exact condition, with the equation where there is one.> |
````

Group by area (one sub-table per aggregate or concern).

*Failure mode:* aspirations ("data is consistent") instead of checkable conditions
("stored total = sum of entries, per account, after every commit").

## 6. Architecture and ownership

Components, what each owns, and what each must not do. A diagram (mermaid) of the main data
flow if there are more than three components.

````markdown
| Component | Owns | Must not |
|---|---|---|
````

Name components by responsibility, not by framework layer names specific to one stack. Include
which component is the authority for each kind of truth (identity, balances, external outcomes).

## 7. Data model and constraints

Entities by responsibility, then the constraints that protect them.

````markdown
| Entity | Responsibility and key fields |
|---|---|
| `<Entity>` | <What it records; identity; the fields rules depend on; mutable vs append-only.> |

| Constraint | Protection |
|---|---|
| Unique on (<fields>) [where <condition>] | <The duplicate or race it prevents.> |
````

Field types by meaning (minor-unit integer, date, instant, enum of named states). State which
rules the store enforces structurally and which live in domain logic.

## 8. State machines

One row or block per aggregate: states, allowed transitions, guard for each, and what is
terminal. Add an operation × state matrix where behavior depends on status.

````markdown
| Aggregate | States and transitions |
|---|---|
| <Aggregate> | `A -> B -> C`; `B -> Failed` only on <definitive condition>. <What an ambiguous outcome does.> |
````

Keep independent dimensions (lifecycle, delinquency, external progress) as separate machines
rather than one combined status.

## 9. Core flows

One subsection per operation that matters. Numbered steps in execution order: validate, lock,
re-read, decide, write, commit — and what commits together. Neutral pseudocode for algorithms.

````markdown
### 9.x <Operation>

1. <Find an earlier result for this operation; an identical retry returns it.>
2. <Acquire … in this order; re-read state after acquiring.>
3. <Validate …; reject with `<Error.Code>` when …>
4. <Apply …>
5. <Commit once. Any failure rolls back all of the above.>
````

State the failure and retry behavior of each flow, not only the happy path.

## 10. Calculation rules

Include when the subject computes money, quantities, rates, dates, scores or allocations.
Cover representation, precision, rounding mode and where it applies, remainder handling, day
counts and calendars, and parsing/formatting at the boundary. Give the formula, then point to a
worked example in Appendix A.

## 11. Transactions, concurrency, and idempotency

- What must be atomic together.
- The concurrency authority and the lock order.
- Idempotency scope: the key, what "same request" means, what a conflicting replay returns.
- Economic deduplication distinct from transport retries.

## 12. External integrations and durable work

Per counterparty: operations, the request identity that makes a resend safe, the outcome
mapping (success / definitive failure / unknown), and what resolves an unknown. Per job:
trigger, unit of work, claim/lease, resumption after a crash.

## 13. Interface surface and errors

Operations, not routes or classes: name, actor, inputs, outputs, errors. If preserving or
changing an existing contract, a source-operation → target-operation mapping with each
intentional difference. An error catalogue with stable codes.

## 14. Security, authorization, and audit

Who may do what, ownership checks, what evidence is immutable, what is retained.

## 15. Observability and operations

Signals that show an invariant is at risk, stuck work, and unresolved external outcomes.
Baseline numbers taken from the source (batch sizes, cadences, timeouts) and which guarantees
the source does **not** establish.

## 16. Testing and acceptance — core

What proves each invariant and each decision. Reference IDs. Separate *characterization* cases
(what the source does today) from *target* cases (what was agreed), so an intended numerical
difference is not mistaken for a regression.

## 17. Implementation phases

Include only if the user wants delivery sequencing.

````markdown
| Phase | Deliverables | Exit evidence |
|---|---|---|
````

## 18. Decision sheet — core

The agreement record. Every finding classified as defect correction, policy change, gap, or
deferral appears here, plus the significant preserved behaviors whose basis is inference.

````markdown
| Decision | Resolution | Implementation obligation |
|---|---|---|
| <Topic> | **Confirmed <date>:** <what was agreed>. | <§ref; what must be built/tested.> |
| <Topic> | **Inferred:** <behavior taken from source>. <What the source does not establish.> | <§ref> |
| <Topic> | **Defect correction:** <source behavior> → <target behavior>. | <§ref; acceptance case.> |
| <Topic> | **Proposed:** <recommendation and the alternative>. Awaiting agreement. | <What is blocked until agreed.> |
| <Topic> | **Deferred <date>:** <interim baseline>. | <Where it is tracked; no gate added.> |
````

Subsections, as needed:

- **18.1 Source evidence** — evidence (file, symbol) → policy carried forward. Note
  precedence where configuration, command defaults and entity defaults disagree.
- **18.2 Defaults and provenance** — configuration, default when omitted, basis. Label each
  value's origin (explicit, configured, source default, chosen fallback).
- **18.3 Confirmed policy changes** — the choice, the confirmed policy, the acceptance
  fixture with numbers.
- **18.4 Open and deferred** — what remains, the interim baseline, who decides.

*Failure mode:* every row reading "Confirmed" when most were read out of the code; a
resolution with no obligation, so nothing changes as a result of the decision.

## Appendix A: Worked examples — core when anything is calculated or corrected

Small, complete, real numbers. One per calculation rule, per corrected defect, and per
confirmed policy change. Show inputs, each step, and the result.

## Appendix B: Source references

The files, symbols, configuration and prior documents consulted, and the limits of the review
(what was not inspected, what could not be established).

## Appendix C: Platform primitive requirements

The capabilities correctness depends on, stated generically (for example: row-level locks with
re-read, partial unique indexes, transaction-scoped advisory locks, atomic outbox, durable
timers), each with the invariant it protects. A port must supply an equivalent for each.
