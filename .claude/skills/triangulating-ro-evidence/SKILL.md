---
name: triangulating-ro-evidence
description: Resolves conflicting or insufficient Ragnarok evidence across stock-iRO captures/runtime/tests, Athena.NET, pinned rAthena, pinned OpenKore, and optional historical eAthena. Use when two or more sources disagree or confidence is materially unclear. Do not use for routine work when authoritative evidence already establishes the answer.
---

# Triangulating Ragnarok Evidence

## Use when

Use when credible evidence sources disagree, point to different semantics, or leave a material confidence gap that affects a decision.

## Do not use when

Do not use when verified stock-iRO/project evidence already resolves the question and no material conflict remains.

## Escalate to

- `reverse-engineering-iro-packets` when the unresolved issue specifically requires new packet/field discovery or differential captures.
- `debugging-stock-client-failures` if the conflict is embedded in a broader runtime failure.

## Hand off to

- Return a ranked evidence conclusion to the invoking workflow.
- `verifying-iro-protocol` when the conclusion is ready to be checked against code.
- `implementing-athena-features` when no further design/research gate remains.


## Purpose

Turn multiple imperfect sources into one explicit conclusion without treating source agreement as a vote.

Athena.NET is iRO-first. The highest-quality evidence wins even when several lower-quality references disagree.

## Mandatory reading

Before reaching a conclusion:

1. For client-facing claims, read the relevant section of `ai/iro-2026-wire.md`.
2. Read the relevant workstream documentation and tests.

## Evidence priority

For **stock iRO client-facing behavior**, use this order:

1. Successful official stock-iRO capture from the targeted client generation.
2. Repeatable runtime behavior of the unmodified official iRO client against Athena.NET.
3. Athena.NET regression tests derived from verified captures/behavior.
4. Current iRO-specific community evidence that agrees with observed client behavior.
5. Pinned `legacy/rathena` and pinned `legacy/openkore` as reference material.
6. Historical eAthena for lineage only.
7. Hypothesis, explicitly labeled unproven.

Do not use majority voting. Three legacy sources do not overrule one verified stock-iRO capture.

## Source roles

### Athena.NET

Use current source/tests/docs to determine:

- already-proven project behavior;
- current authority boundaries;
- deliberate divergences from legacy implementations;
- known disproven assumptions.

Current code and tests can be newer than architecture documents. Verify the implementation, not just prose.

### rAthena

Use pinned `legacy/rathena` primarily for:

- mature server mechanics;
- validation/failure semantics;
- state transitions;
- persistence/schema concepts;
- script behavior;
- data formats;
- implementation relationships.

Do not assume its packet ID/layout is correct for current iRO.

### OpenKore

Use pinned `legacy/openkore` primarily for:

- packet names;
- client/parser expectations;
- field interpretations;
- regional/iRO overrides;
- send/receive mappings;
- community protocol clues.

Inspect `legacy/openkore/src/Network/Receive/` and `legacy/openkore/src/Network/Send/` (including `iRO.pm` and the `iRO/` subdirectory in each), `legacy/openkore/tables/servers.txt` (iRO `serverType` entries), and `legacy/openkore/tables/iRO/`.

Do not treat bot behavior as authoritative official-server behavior.

### eAthena

eAthena is **not a current Athena.NET submodule**. If consulted externally, use it only as `HISTORICAL` evidence to answer questions such as:

- Was a rAthena behavior inherited from older Athena lineage?
- Is a surprising branch longstanding legacy behavior or a later addition?
- Did a semantic concept predate the current rAthena implementation?

Never use eAthena to establish 2026 iRO wire compatibility.

## Confidence labels

Label each material claim:

- `VERIFIED` — directly established by authoritative iRO evidence or an exact project test grounded in it.
- `SUPPORTED` — strongly supported by multiple references but not directly proven by stock-iRO evidence.
- `HISTORICAL` — useful lineage evidence only.
- `HYPOTHESIS` — plausible but unproven.
- `CONFLICT` — material sources disagree and no higher-authority evidence resolves it.

Do not silently upgrade `SUPPORTED` or `HYPOTHESIS` to fact during implementation.

## Workflow

1. State the exact claim being investigated.
2. Collect only evidence relevant to that claim.
3. Record source, exact path/symbol/packet/frame when available.
4. Separate:
   - wire-format evidence;
   - server-semantic evidence;
   - historical evidence.
5. Identify contradictions.
6. Apply the evidence priority.
7. State the conclusion and confidence.
8. State what observation would falsify the conclusion.
9. If still uncertain, define the smallest experiment or capture needed.

## Output

For substantial or reusable work, use [EVIDENCE-MATRIX.md](EVIDENCE-MATRIX.md). For a small focused task, preserve the same reasoning structure but report directly unless a persistent artifact is useful.

A useful conclusion is not “rAthena and OpenKore agree.” It is:

> `SUPPORTED`: both references interpret the field as X; no verified iRO capture currently proves the field. Do not serialize or validate against X until capture/runtime evidence exists.

Keep auth tokens, credentials, and opaque sensitive capture payloads out of committed evidence.
