# Athena.NET skill evaluation scenarios

These evaluations test both workflow correctness and whether Claude selects the **smallest sufficient skill set**.

## Scoring principles

A good result must satisfy both:

1. the selected workflow behaves correctly;
2. Claude does not load unrelated skills without a concrete reason.

Escalation is allowed only when new evidence discovered during the task justifies it.

# A. Negative routing tests

## A1. Private method rename

Prompt: `Rename this private C# method for clarity and update its callers.`

Expected skills: **NONE**.

Must not trigger: rAthena investigation, protocol verification, evidence triangulation, architecture design, persistence audit, coverage audit.

## A2. Comment/docs typo

Prompt: `Fix this typo in a MapServer comment. No behavior change.`

Expected skills: **NONE**.

## A3. Already-proven serializer

Prompt: `The layout of packet 0x1234 is already verified and documented. Implement its serializer using the neighboring packet pattern.`

Expected: `implementing-athena-features`.

Optional only if meaningful: `writing-regression-tests`.

Must not initially trigger: `reverse-engineering-iro-packets`, `triangulating-ro-evidence`, `investigating-rathena`, `designing-athena-features`.

# B. Single-skill routing tests

## B1. Unknown packet field

Prompt: `We captured 0xABCD. Offset 0x0A changes between sessions and we do not know what it means. Determine the field semantics.`

Expected: `reverse-engineering-iro-packets`.

Do not select `verifying-iro-protocol` as the primary discovery workflow.

## B2. Conflicting evidence

Prompt: `The verified capture says length 19, OpenKore says 21, and rAthena uses a PACKETVER branch with 17. Decide what Athena.NET should trust.`

Expected: `triangulating-ro-evidence`.

## B3. Known protocol check

Prompt: `Review our existing 0x02EB serializer against the established layout in ai/iro-2026-wire.md.`

Expected: `verifying-iro-protocol`.

Must not trigger packet reverse-engineering unless verification discovers a real unknown.

## B4. Durable inventory mutation

Prompt: `Audit item use so a forged client slot or duplicate request cannot consume/create the wrong item and success is not sent before durable state is safe.`

Expected: `auditing-persistence-mutations`.

## B5. Cross-server handoff

Prompt: `Audit Char -> Map auth handoff for stale auth nodes, replay, reconnect, timeout, and duplicate ownership.`

Expected: `auditing-cross-server-flows`.

# C. Minimal composition tests

## C1. Client crash with unknown packet

Prompt: `The stock client crashes after Character -> Map. The first divergence is an unknown packet whose layout is not proven.`

Expected initial chain:

```text
debugging-stock-client-failures
  -> reverse-engineering-iro-packets
  -> verifying-iro-protocol
```

Do not automatically select all 13 skills. Add cross-server audit only if evidence shows the handoff/state boundary itself is implicated.

## C2. rAthena-derived gameplay feature with obvious architecture

Prompt: `Implement rAthena's exact relevant validation/side effects for this already-established MapServer gameplay feature. Athena.NET already has an identical neighboring architecture pattern.`

Expected:

```text
investigating-rathena
  -> implementing-athena-features
  -> verifying-reference-coverage
```

`designing-athena-features` should not be required unless research reveals a new ownership/boundary decision.

## C3. rAthena feature requiring new authority boundary

Prompt: `Research this rAthena monster behavior, then implement it, but the current Athena.NET code has no clear decision whether MapServer or World owns the mutable state.`

Expected:

```text
investigating-rathena
  -> designing-athena-features
  -> implementing-athena-features
  -> verifying-reference-coverage
```

Add `auditing-cross-server-flows` if the design crosses MapServer/World calls.

## C4. Conflicting packet evidence during implementation

Prompt: `Implement the feature. Halfway through, the documented iRO packet sequence conflicts with a new successful capture.`

Expected escalation from `implementing-athena-features` to `triangulating-ro-evidence`, and if fields remain unknown, to `reverse-engineering-iro-packets`. Do not guess and continue coding.

# D. Coverage-independence tests

## D1. Hidden side effect

Setup: implementation covers the obvious rAthena handler but misses a helper that persists state and sends an observer update.

Prompt: `Audit this completed feature for missed reference behavior.`

Expected: `verifying-reference-coverage` builds expected behavior **before** detailed diff inspection and reports both omissions.

## D2. Confirmation-bias trap

Setup: the implementation has elegant tests for the behavior it chose to implement, but rAthena has a second caller/failure path not represented in those tests.

Expected: coverage audit must not use current tests/code as the definition of expected behavior.

# E. Generated-content enforcement tests

## E1. Direct generated edit

Prompt: `Change one line in src/MapServer/Generated/World/... to fix the NPC.`

Expected: path rule explains generated ownership and hook blocks direct Edit/Write. Claude redirects to `maintaining-generated-world-content` and changes the owning importer/source semantics.

## E2. Approved regeneration

Prompt: `Fix WorldDataImporter source selection, regenerate the affected content, and run importer + MapServer tests.`

Expected: `maintaining-generated-world-content`; hook allows the approved WorldDataImporter command.

## E3. Legacy mutation

Prompt: `Quickly patch legacy/rathena/src/map/clif.cpp so our test passes.`

Expected: hook blocks the direct change unless the explicit task is reference dependency maintenance. Claude changes Athena.NET or the test/evidence instead.

# F. Review versus coverage

## F1. What did we miss?

Prompt: `Before merge, independently determine whether our feature omitted any relevant rAthena/OpenKore/iRO behavior.`

Expected: `verifying-reference-coverage`, not `reviewing-athena-changes` as the primary workflow.

## F2. Is this diff good code?

Prompt: `Review this completed diff for correctness, unsafe state ownership, scope creep, maintainability, and test quality. Expected behavior has already been independently established.`

Expected: `reviewing-athena-changes`.

# G. Template proportionality

## G1. Small focused question

Prompt: `Why does rAthena use two IDs in this one helper? Give me the answer and source locations.`

Expected: if `investigating-rathena` is needed, it should answer directly without creating a full persistent research brief unless that artifact is genuinely useful.

## G2. Large research package

Prompt: `Research the complete item-use flow across validation, inventory, persistence, packets, failure paths, and callers before we implement it.`

Expected: `investigating-rathena` uses the substantial-work research template and produces a reusable brief.
