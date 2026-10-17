# Test audit campaign

Use this mode for an explicitly scoped audit of a subsystem across its test
surface. Apply the value bar, retention bar, candidate evidence, and validation
in [SKILL.md](SKILL.md). Optimize for preserved confidence and lower maintenance
cost, not a target number of deletions.

## 1. Scope and baseline

Identify the production responsibilities, contracts, test suites, and shared
support in scope using the repository's actual structure. Include relevant
integration, UI, platform, architecture, or cross-component tests where present;
do not assume a fixed project list or folder layout.

Record the baseline revision and working-tree state, runner and framework setup,
required infrastructure, and results for in-scope suites. Record declared tests
and executed/discovered cases separately when useful: parameterized, generated,
and property-based tests do not map reliably to source attribute counts. Use
runner discovery where supported; label source-only inventories as estimates.

List baseline failures and unavailable suites separately. If the baseline cannot
build or run, continue read-only discovery where useful, but do not claim verified
coverage preservation for that surface.

Proceed to editing only when the available baseline and its gaps are understood.

## 2. Review groups and ledger

Group tests by production responsibility and contract, across project boundaries
where necessary. Identify shared fixtures and support consumed by several groups.
Read each assigned test and its meaningful data cases, production owner, setup,
and assertions. Consult callers and history where relevant.

Create a ledger with one disposition per test or independently meaningful case:

- **Retain:** name the contract and credible failure it catches.
- **Repair:** retain the contract but improve setup, assertions, or determinism.
- **Consolidate:** name the keeper and cases or assertions it must absorb.
- **Delete:** name the remaining proof or why no valuable contract exists.
- **Investigate:** record missing evidence or environment access; retain for now.

Use the candidate evidence from `SKILL.md` for proposed edits. Mark rows or
generated cases separately when a single parameterized declaration spans
different dispositions. Judge the actual oracle, not the test name.

For a large or high-risk campaign, use independent review or parallel read-only
groups when delegation is available and authorized. Otherwise perform a separate
preservation pass. Keep shared edits coordinated so reviews do not overwrite
each other's work.

Discovery is complete when every in-scope test or case has a disposition and
supporting evidence, including explicit unknowns.

## 3. Preservation plan and edits

Review the ledger by contract before treating it as an edit list. Identify what
proof remains for each removed assertion and whether its boundary can detect
the same failure. Favor sufficient fidelity and useful feedback over an automatic
preference for the broadest or slowest suite. Preserve distinct unit, integration,
and end-to-end risks even when they exercise the same production path.

Repair keepers and add missing cases before removing superseded tests. Edit in
coherent responsibility groups; coordinate changes to shared fixtures, support,
and architecture checks. Remove obsolete helpers only after checking all consumers.
Check public or indirectly invoked production members before classifying them
as dead code. Keep local build, analyzer, and configuration policies intact.

Run the focused checks after each group and consumer suites after shared changes.
Keep unavailable or uncertain candidates out of deletion batches.

This phase is complete when planned edits are applied, keepers cover the retained
contracts, and affected checks pass or their unresolved limits are recorded.

## 4. Preservation review and handoff

Compare removed coverage with the keepers, checking distinct inputs, failure
modes, target frameworks, platforms, and fixture guarantees. Inspect negative
controls for failures caused by unrelated guards and assertions that cannot fail.
Use independent review when available and authorized for the risk involved.

For uncertain or high-risk replacement proof, use a targeted reversible mutation
or a pre-fix control to verify that the keeper detects the intended regression.
Isolate these experiments from unrelated changes and restore all experimental
edits. Report checks that could not be demonstrated.

Investigate baseline failures that affect keepers. Repair defects within scope;
record unrelated bugs and environmental blockers as follow-ups. Do not remove
failing tests merely to obtain a green result.

If the base branch changes during the campaign, follow the repository's branch
workflow, review incoming changes, and reconcile contracts added to files slated
for deletion. Preserve those contracts in an appropriate owner and revalidate the
affected surface rather than mechanically keeping either side of a conflict.

Run the required broader checks and inspect the final diff. Use the `SKILL.md`
handoff, plus:

- baseline and final inventories, distinguishing declarations from executed cases;
- responsibility groups, keeper tests, and any retired support or redundant layers;
- preservation gaps found and the controls used to check replacement proof;
- unresolved product defects, unverified suites, and retained uncertain candidates;
- production, test, and support change sizes when useful for reviewing the campaign.

Claim completion only for the audited scope and validation actually performed.
