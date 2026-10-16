---
name: reverse-engineering-iro-packets
description: Reverse-engineers unknown or partially known stock-iRO packet IDs, layouts, fields, lengths, ordering, or semantics using captures and controlled evidence. Use when the wire behavior itself is unknown or a documented assumption is contradicted. Do not use merely to verify an already-understood packet implementation.
---

# Reverse-Engineering iRO Packets

## Use when

Use when a packet ID, layout, field offset/type/meaning, length rule, ordering rule, or state boundary is unknown or contradicted by the official client.

## Do not use when

Do not use when the relevant wire behavior is already established and the task is only implementation or verification against known evidence.

## Escalate to

- `triangulating-ro-evidence` when multiple credible sources disagree.
- `debugging-stock-client-failures` when the unknown packet was discovered from a broader crash/hang/disconnect investigation.

## Hand off to

- `verifying-iro-protocol` once the unknowns are sufficiently established to verify an implementation.
- `implementing-athena-features` once required semantics are known.


## Purpose

Convert unknown bytes into evidence-backed protocol facts without guessing.

## Before analysis

1. Read `ai/iro-2026-wire.md`.
2. Search the existing tests and packet code so known facts are not rediscovered.
3. Search pinned OpenKore and rAthena for the packet ID in:
   - hexadecimal form;
   - packet name;
   - nearby related packets;
   - regional/PACKETVER tables.
4. Treat reference matches as clues until verified against stock iRO.

## Capture-first workflow

### 1. Establish packet boundaries

Determine:

- stream direction;
- packet ID;
- fixed/variable framing;
- total length;
- internal length field, if proven.

Do not infer field semantics before packet boundaries are reliable.

### 2. Correlate known values

Look for values already known in the same session:

- account ID;
- character ID;
- login/session IDs;
- map coordinates;
- tick/time;
- item/skill IDs;
- counts;
- names;
- stat values.

Search exact little-endian byte representations where appropriate.

### 3. Use differential evidence

Prefer controlled pairs/multiples:

- same account, different character;
- same character, different map/position;
- same action with one amount/ID changed;
- reconnect/new session;
- empty versus one-entry versus multi-entry list;
- enabled versus disabled option.

A field that changes with one controlled variable is stronger evidence than a plausible static match.

### 4. Compare references

Use OpenKore for naming/layout/client interpretation clues.

Use rAthena for server-side intent, structures, and generation logic.

Use eAthena only when historical lineage could explain a surprising inherited shape.

### 5. Classify every field

Use:

- `VERIFIED`
- `SUPPORTED`
- `UNKNOWN`
- `CONFLICT`

Do not name an unknown field after a reference merely because the size fits.

## Hypothesis discipline

For each hypothesis state:

- evidence for it;
- evidence against it;
- prediction it makes;
- smallest experiment that would falsify it.

Change one variable per experiment where possible.

## Diagnostic implementation

When a diagnostic Athena.NET change is needed:

- keep it minimal and reversible;
- do not weaken authentication/ownership checks;
- do not hard-code captured identity/state as production behavior;
- do not replay opaque auth material;
- do not patch the official client;
- do not commit sensitive capture bytes.

## Promote to project fact only when ready

A conclusion may enter `ai/iro-2026-wire.md` or a regression fixture only after its evidence level is explicit.

When a prior assumption is disproven, document the disproven assumption too. This prevents future agents from rediscovering and reintroducing it.

## Output

For substantial or reusable work, use [PACKET-EVIDENCE-LEDGER.md](PACKET-EVIDENCE-LEDGER.md). For a small focused task, preserve the same reasoning structure but report directly unless a persistent artifact is useful.

Finish with:
- facts safe to implement;
- unknowns that must remain opaque;
- experiments still needed.
