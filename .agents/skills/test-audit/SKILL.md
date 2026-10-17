---
name: test-audit
description: "Write, change, review, or audit tests in .NET projects. Evaluate behavioral value, regression coverage, duplication, implementation coupling, and test-support code using the repository's existing test framework and runner. Use for focused test work or broader suite cleanup; do not start an unrelated test sweep during ordinary production changes."
---

# Test Audit

Improve confidence per unit of maintenance cost. Apply the same value bar when
authoring tests and auditing existing ones. Adapt to the repository's languages,
application type, frameworks, runners, and infrastructure; do not require a
particular architecture or package stack.

- **Authoring:** evaluate new or changed tests within the requested task.
- **Focused audit:** inspect a bounded area and make evidence-backed repairs,
  consolidations, or deletions when requested. A review-only request stays read-only.
- **Campaign:** audit a subsystem across its test suites. Read
  [CAMPAIGN.md](CAMPAIGN.md) only for this broader mode.

## Discover the local test setup

Read applicable repository instructions and nearby test conventions. Inspect the
relevant solution and project files, SDK selection, shared build/package settings,
runner settings, test configuration, and CI or local test scripts when present.
Do not assume a `tests/` folder, a solution file, C# only, or an SDK-style project.

Identify:

- test frameworks, assertions, mocking libraries, adapters, and runner versions;
- target frameworks, configurations, operating-system requirements, and platform
  workloads, including legacy .NET Framework or multi-targeted projects;
- real dependencies, in-memory providers, fakes, host fixtures, and shared support;
- setup, isolation, parallel execution, cleanup, and external-service requirements;
- established test commands, discovery/filter syntax, and CI checks.

Keep the existing stack, whether it uses xUnit, NUnit, MSTest, another framework,
VSTest, Microsoft.Testing.Platform, or a repository-specific runner. Do not install
packages, migrate runners, or restructure projects just to apply this skill.

Map the actual boundaries relevant to the task. These are examples, not required
projects or layers:

| Boundary | Can establish | Important limit |
|---|---|---|
| Isolated logic | calculations, state transitions, validation, and error decisions | Does not establish external-system behavior. |
| Component with controlled collaborators | orchestration, retries, failure handling, and meaningful interactions | Doubles must not supply the behavior being proved. |
| Infrastructure or integration | provider semantics, persistence, transactions, serialization, and host wiring | Fidelity depends on the actual provider and fixture. |
| Public entry point or end-to-end flow | transport contracts, user-visible behavior, and cross-component lifecycles | Broad assertions can obscure a missing lower-level guarantee. |
| Static, architecture, or compatibility check | layering, API surface, packaging, generated output, and release contracts | Needs an independent rule, not a copy of implementation details. |
| Shared test support | deterministic inputs, isolation, and reusable fixtures | Supporting code is not itself proof of product behavior. |

## Authoring gate

Before adding or materially changing a test, identify:

1. The observable behavior, invariant, or independent contract it protects.
2. A credible regression that would make it fail for the intended reason.
3. What it adds to existing coverage: a new case, boundary, failure mode, or
   useful fast diagnosis that another test does not already provide.
4. Whether its setup and assertions reach the owner of that behavior without
   constructing the expected result in a fake or fixture.

Choose the least costly boundary that can faithfully prove the contract. Use a
real dependency when its semantics are the claim, such as a database constraint
or transaction; a mock or incompatible in-memory provider cannot establish it.
Broader tests and focused unit tests can both be valuable. Do not remove a layer
just because another layer exercises the same path.

Prefer extending an existing parameterized test when cases share setup and
assertions. Keep separate tests when they express different contracts or need
different fixtures. Follow local naming and data conventions rather than
requiring particular attributes or a naming pattern.

Assert meaningful results, effects, errors, or interactions. Call counts and
ordering are valid when they express a contract, such as one charge, retry
limits, or cleanup before disposal. Merely mirroring internal call shape makes
a test fragile under behavior-preserving refactoring.

Use controllable time, randomness, and scheduling where suitable. A fixed sleep
is usually a poor synchronization mechanism; prefer deterministic coordination
or bounded condition waits for real asynchronous systems. Timing itself may be
the contract in a timeout or performance test.

Avoid adding public members, flags, wrappers, or access bypasses solely to reach
private implementation. Dependency injection, clocks, internal access, and other
testability seams can be legitimate design choices; assess their purpose and
cost before replacing or removing them.

For bug regressions, demonstrate that the test fails against the old behavior
and passes after the fix when practical. Use an isolated checkout, targeted
reproduction, or reversible mutation without disturbing unrelated working changes.
Verify the failure reason; if a before-fix run is unavailable, report the limit
instead of claiming the test was proven to catch the bug.

## Low-value patterns to investigate

These are evidence prompts, not automatic deletion rules:

- No meaningful oracle: executing code only for coverage, self-comparison, or
  expected values computed by the code under test. No-exception assertions can
  still protect a defined smoke, startup, or compatibility contract.
- Setup supplies the claimed behavior: mocks return exactly what is asserted,
  fixtures perform the persistence under test, or callbacks manufacture ordering.
- Implementation inventories: copied lists, private-member assertions, source
  greps, or snapshots that change for harmless reorganization without protecting
  an independent contract.
- Duplication without distinct value: repeated scenarios or helper tests whose
  remaining owners already detect the same regressions with suitable feedback.
- False confidence: a negative case rejected by an earlier unrelated guard, a
  persistence assertion against untouched storage, or a test name that promises
  rollback, isolation, or authorization the input never exercises.
- Fragile nondeterminism: arbitrary sleeps, uncontrolled clocks or randomness,
  shared mutable state, or order dependencies unrelated to the contract.
- Orphaned support: helpers, wrappers, or production paths retained only because
  obsolete tests reference them. Lack of a direct caller does not prove dead code.

## Retention bar

Keep tests that independently protect public APIs, protocols, security boundaries,
storage semantics, migrations, configuration defaults, platform compatibility,
generated artifacts, rendered output, package/release behavior, or architecture.
Static checks, snapshots, interaction assertions, and slow tests are not inherently
low value; assess the contract and whether their oracle is independent.

Keep credible regressions and useful diagnostic coverage. Source inspection may
be appropriate when the source or a structural rule is itself the contract and
there is no better supported inspection mechanism. Tests for a shared fake or
fixture can be valuable when its behavior underpins many other tests.

Treat an existing failure as evidence to investigate, not a reason to delete the
test. Distinguish a product bug, incorrect expectation, broken environment, and
flaky fixture. Report unrelated defects; repair product behavior only within the
authorized task scope.

## Focused audit and candidate evidence

Inspect the complete candidate, its data cases, fixtures, and production owner.
Trace relevant entry points and collaborators, then compare overlapping coverage.
Inspect callers and history when they clarify intent or whether code is removable.
When a claim depends on a library or provider, verify its behavior from the
installed version, documentation, or source rather than the test name.

Organize discovery around production responsibilities and contracts. Include
shared support and cross-component coverage only where they affect the area.
Prefer a few high-confidence findings over speculative deletion lists.

Before editing an audit candidate, record:

- exact test identity, file, and relevant parameterized cases;
- the failures it can actually detect and evidence for the proposed action;
- the remaining proof, including its assertions and boundary, or why there is
  no independently valuable contract to retain;
- callers and purpose of any support or production code proposed for removal;
- relevant history if available, uncertainties, and preservation risks;
- the focused validation command and required environment.

If evidence is insufficient, retain the candidate or report what needs investigation.
Repair an inadequate assertion when its contract is valuable. Consolidate only
after the keeper covers the distinct cases and failure modes being removed.

## Edit and validate

Keep each batch coherent. Remove newly unused support when its callers and
purpose are understood. Check indirect consumers before deleting production
members: public APIs, reflection, dependency-injection registration, generated
code, configuration, and consumers outside the repository may not appear in a
simple reference search. Do not pursue negative line counts as a goal.

Honor the repository's analyzer and warning policy. Clean up unused code without
disabling checks or introducing suppressions to accommodate the audit. Change
configuration only when justified by the task and applicable conventions.

Derive validation from the detected environment:

1. Establish a baseline for affected tests when auditing or changing existing
   behavior; separate baseline failures from newly introduced ones.
2. Build and run the affected tests using supported SDK, framework, configuration,
   runner, and working-directory conventions. Ensure filters discover and execute
   the intended cases; a successful run with zero tests is not validation.
3. Run consumers of changed shared support and relevant integration/platform
   suites. For multi-targeted projects, cover affected targets or explain gaps.
4. Expand to the repository's required checks or broader suite when the change
   warrants it, especially shared fixtures or campaigns. Record service, platform,
   credentials, or tooling blockers rather than replacing real proof with mocks.
5. Inspect the final diff and run `git diff --check` when using Git.

Do not assume `dotnet test` arguments are portable between VSTest and
Microsoft.Testing.Platform or between SDK and framework versions. Use repository
scripts, installed runner help, and version-matched documentation to select
project/solution selectors, filters, and argument forwarding. Legacy projects
may require platform-specific build tools and runners. Rebuild changed code
before using options that skip the build.

## Handoff

Summarize contracts added, repaired, consolidated, or removed; support or
production simplifications; valuable lookalikes retained; and unresolved findings.
Report exact validation commands, executed test results, and unrun checks with
their reasons. For larger audits, distinguish production changes from test and
support changes. Mention branch or commit state only when relevant; follow the
user's existing workflow rather than imposing a landing or branch policy.
