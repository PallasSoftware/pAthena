---
name: writing-regression-tests
description: Creates focused Athena.NET regression tests for proven protocol, gameplay, persistence, distributed-state, bug-fix, and generated-content invariants. Use when a discovered behavior or failure mode must be protected from recurrence. Do not use merely to increase test count or for trivial refactors with no meaningful behavioral invariant.
---

# Writing Regression Tests

## Use when

Use when a proven behavior, bug, edge case, wire fact, authority rule, persistence rule, or generated-content invariant should fail loudly if it regresses.

## Do not use when

Do not use just to add coverage numbers, and do not create expensive integration tests when a smaller test can prove the actual invariant.

## Escalate to

- `verifying-iro-protocol` if the test depends on wire facts that are not yet established.
- `auditing-persistence-mutations` or `auditing-cross-server-flows` if the invariant cannot be stated safely without first resolving mutation/boundary semantics.

## Hand off to

- Return to the invoking implementation/review workflow after the focused tests pass.
- `reviewing-athena-changes` for final substantial-change review.


## Purpose

Encode **why the discovered behavior matters**, not merely mirror the current implementation.

A useful regression test fails if the original bug or omission returns.

## Choose the lowest useful layer

Prefer the smallest layer that proves the invariant:

- packet framing test;
- parser test;
- serializer byte-exact test;
- handler/session test;
- gameplay/domain-rule test;
- persistence test;
- cross-server contract/integration test;
- World/Orleans authority test;
- world-data compiler/converter test;
- generated-content coverage test;
- end-to-end stock-iRO session integration where lower layers cannot prove it.

Do not use an expensive integration test for an invariant a pure unit test can prove, but do not unit-test away a cross-boundary bug.

## Evidence-linked assertions

For protocol tests, assert concrete facts:

- packet ID;
- total length;
- field offsets;
- byte order;
- ordering;
- fragmentation/coalescing behavior.

For gameplay tests, assert:

- authoritative input resolution;
- validation/rejection;
- state mutation;
- persistence semantics;
- idempotency/stale request handling;
- observer/client projection only after valid mutation.

For generated data, assert:

- full source selection;
- generic/base plus duplicate-family coverage;
- deterministic output;
- source provenance;
- unsupported constructs fail/report instead of partially generating.

## Avoid implementation-mirroring tests

Weak:

> call helper X and assert it returns the same constant helper X contains.

Strong:

> serialize one proven state and assert the exact bytes/offsets the stock client requires.

Weak:

> mock every service and assert an internal method was called.

Strong:

> forged client slot/amount is rejected and authoritative inventory remains unchanged.

## Capture-derived fixtures

When using capture evidence:

- minimize fixtures to the bytes required for the invariant;
- sanitize account IDs when semantics permit;
- never commit credentials, tokens, or sensitive opaque auth blobs;
- document provenance in a safe form;
- preserve exact bytes only when necessary for the assertion.

## Naming

Name the test after the invariant or bug:

`ReadNextPacketAsync_Reassembles0c1fAcrossFragmentedReads`

is better than:

`TestPacket1`

A future reviewer should understand the regression from the test name and assertion.

## Test execution

Run focused tests first, then the owning project test suite.

Common project suites include:

- `tests/LoginServer.Tests/LoginServer.Tests.csproj`
- `tests/CharServer.Tests/CharServer.Tests.csproj`
- `tests/MapServer.Tests/MapServer.Tests.csproj`
- `tests/World.Tests/World.Tests.csproj`
- `tests/WorldDataImporter.Tests/WorldDataImporter.Tests.csproj`

Then run the relevant build and `git diff --check`.

## Output

In the completion summary, state:

- invariant protected;
- original failure mode;
- test location;
- commands run;
- why this test would catch a regression rather than merely exercise code.
