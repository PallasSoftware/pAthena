---
name: implementing-athena-features
description: Implements already-understood Athena.NET behavior using the current C# architecture, tests, and project evidence. Use when required behavior and any non-trivial design decisions are established and the task is ready for code. Do not use while material protocol semantics, reference behavior, state ownership, or architecture decisions are still unknown.
---

# Implementing Athena.NET Features

## Use when

Use when the required behavior is established, material uncertainties are resolved, and the task is ready for production code and tests.

## Do not use when

Do not use as a substitute for research or design when packet semantics, relevant reference behavior, state ownership, persistence ordering, or architecture are still materially uncertain.

## Escalate to

- `investigating-rathena` if implementation uncovers missing reference semantics.
- `reverse-engineering-iro-packets` if an unknown wire field/sequence becomes blocking.
- `triangulating-ro-evidence` if evidence conflicts.
- `designing-athena-features` if the change unexpectedly requires a new ownership/boundary decision.

## Hand off to

- `verifying-reference-coverage` for an independent omission audit.
- `verifying-iro-protocol` when client wire behavior changed.
- `writing-regression-tests` for behavior that needs durable protection.
- `reviewing-athena-changes` before merge for substantial work.


## Purpose

Implement the **verified behavior** with the smallest correct change that fits Athena.NET.

This is not a mechanical rAthena-to-C# translation skill.

## Entry gate

Before editing production code, verify:

- The applicable project/rule invariants and only task-relevant `ai/` evidence were considered.
- Relevant current code/tests/config were inspected.
- `git status` and `git diff` were inspected.
- A research brief exists for behavior derived from rAthena/reference code.
- An Athena.NET design exists for non-trivial changes.
- Material client-wire assumptions are `VERIFIED`, not merely inferred from rAthena/OpenKore.

If these are missing, perform the required research/design first.

## Implementation rules

1. Follow existing repository patterns.
2. Implement every verified required behavior from the research/design checklist.
3. Do not implement generic kRO/rAthena compatibility solely for parity.
4. Do not copy rAthena architecture merely because it is the reference.
5. Do not trust client-supplied gameplay state.
6. Preserve validation and negative/failure paths, not only the happy path.
7. Preserve state ownership. Do not introduce a second authoritative copy.
8. Preserve idempotency/replay protections where the operation can repeat.
9. Preserve async/disconnect/stale-session semantics where relevant.
10. Avoid unrelated refactors.
11. Reuse existing services/types before adding abstractions.
12. Prefer explicit C# over clever generic machinery for protocol-critical code.
13. Do not weaken validation just to make a packet/session “work.”
14. Do not log credentials, tokens, sensitive opaque auth payloads, or unsanitized captures.

## Wire behavior

When touching client packets:

- use `ai/iro-2026-wire.md` and verified tests/captures first;
- keep packet ID, length, offsets, byte order, encoding, and ordering explicit;
- do not copy a packet layout from rAthena/OpenKore without iRO evidence;
- preserve TCP framing correctness under fragmentation and coalescing;
- mark any unavoidable unknown field as unknown rather than inventing semantics.

## Generated content

Never hand-edit generated C# under `src/MapServer/Generated`.

If generated output must change:

1. identify the pinned rAthena source;
2. change importer/compiler/source-selection semantics as appropriate;
3. regenerate deterministically;
4. verify source provenance;
5. run importer and MapServer tests.

Use `maintaining-generated-world-content` for non-trivial generated-content changes.

## Persistence

For durable client actions:

- resolve authoritative entities/state first;
- validate ownership, amount, slot/range/state, and stale requests;
- persist required durable mutation before reporting success;
- update runtime authoritative state in the project-defined order;
- notify/project to the client only after required durable success.

Avoid partial state when a downstream step fails.

## Work loop

1. Implement one coherent slice.
2. Build the smallest affected project.
3. Run focused tests.
4. Fix actual failures, not speculative ones.
5. Run the broader relevant test project.
6. Compare the completed code against the implementation checklist.
7. Inspect `git diff` for accidental scope expansion.
8. Run `git diff --check`.
9. Hand off to independent reference/protocol verification.

## Completion report

Report:

- behavior implemented;
- files changed;
- tests added/updated;
- commands executed and outcomes;
- intentional divergences from rAthena;
- remaining unknowns;
- any checklist item not implemented and why.

A green build is necessary, not sufficient. The feature is not done until post-implementation coverage verification passes.
