---
name: verifying-iro-protocol
description: Verifies an existing or proposed Athena.NET protocol implementation against established stock-iRO evidence, including framing, IDs, lengths, offsets, encoding, ordering, and state transitions. Use when checking a parser, serializer, sequence, or handoff whose expected wire behavior is already substantially known. Do not use as the primary workflow for discovering unknown packet semantics.
---

# Verifying iRO Protocol

## Use when

Use when expected wire behavior is already substantially known and an existing/proposed parser, serializer, framing rule, packet sequence, or handoff must be checked against that evidence.

## Do not use when

Do not use as the primary discovery workflow when packet fields, lengths, semantics, or sequencing are still materially unknown.

## Escalate to

- `reverse-engineering-iro-packets` if verification exposes unknown wire facts.
- `triangulating-ro-evidence` if credible sources conflict.
- `debugging-stock-client-failures` if the protocol check is only one part of a larger observed runtime failure.

## Hand off to

- `implementing-athena-features` for a proven required correction.
- `writing-regression-tests` for byte-exact/framing/sequence invariants.


## Authority

For stock-iRO wire behavior, `ai/iro-2026-wire.md`, verified captures, repeatable official-client runtime behavior, and capture-derived tests outrank rAthena/OpenKore packet tables.

Read `ai/iro-2026-wire.md` and only the task-relevant workstream documentation before making wire claims.

## Packet verification checklist

For every packet involved, verify as applicable:

- packet ID;
- fixed versus variable length;
- total length;
- location/type of an internal length field if present;
- field offsets;
- signedness;
- integer width;
- little-endian encoding where applicable;
- string encoding;
- fixed-width string padding;
- null termination;
- packed position/bit-field representation;
- array entry size and count math;
- opaque/reserved bytes;
- correlation IDs/session IDs;
- state preconditions;
- whether the packet is client-to-server or server-to-client;
- response ordering;
- duplicate/repeated packet semantics;
- timing/state boundary that triggers it.

## Stream verification

Do not equate one TCP read with one packet.

Verify framing under:

- fragmented delivery;
- multiple packets coalesced in one read;
- variable-length packet boundaries;
- incomplete header/body;
- malformed length;
- disconnect mid-packet.

When diagnosing a sequence, reconstruct the complete relevant TCP byte stream before blaming one packet.

## Evidence ledger

For every field or behavior, classify:

- `VERIFIED` — capture/runtime/project-test proven.
- `SUPPORTED` — consistent secondary evidence but not capture-proven.
- `UNKNOWN` — semantics deliberately unknown.
- `CONFLICT` — evidence disagrees.

Never convert `UNKNOWN` into a guessed name simply to make a model look complete.

## Cross-reference strategy

Use rAthena to understand server intent and mature packet concepts.

Use OpenKore to discover packet names, parser layouts, iRO/regional overrides, and client-side interpretation clues.

Neither becomes wire authority without matching iRO evidence.

## Size arithmetic

For variable packets, show the arithmetic explicitly.

Example shape:

`total = header_size + entry_count * entry_size`

Then verify:
- captured total;
- serializer total;
- parser bounds;
- zero-entry case;
- maximum reasonable count.

## State-sequence verification

A correct packet with the wrong timing/order is still a protocol bug.

Record:

1. precondition/state;
2. inbound event;
3. server state mutation;
4. outbound packet sequence;
5. next expected client event.

## Security

Do not commit or print:

- passwords;
- PINs;
- bearer/JWT/session/auth tokens;
- unsanitized opaque authentication blobs.

Sanitize capture-derived fixtures.

## Output

For substantial or reusable work, use [PROTOCOL-CHECKLIST.md](PROTOCOL-CHECKLIST.md). For a small focused task, preserve the same reasoning structure but report directly unless a persistent artifact is useful.

If required facts remain unknown, recommend the smallest differential capture/runtime experiment instead of inventing a layout.
