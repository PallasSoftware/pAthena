---
name: debugging-stock-client-failures
description: Diagnoses official stock-iRO client crashes, disconnects, hangs, failed map loads, rejected actions, and Login/Char/Map transition failures. Use when the unmodified client stops progressing or diverges from a known-good flow. Do not use for routine implementation when no client failure exists or for general code-quality review.
---

# Debugging Stock-iRO Client Failures

## Use when

Use when the official unmodified iRO client crashes, disconnects, hangs, returns to an earlier screen, fails to load, or otherwise diverges from a known-good flow.

## Do not use when

Do not use for routine feature implementation with no observed client failure, or as a general review workflow.

## Escalate to

- `reverse-engineering-iro-packets` if the first divergence involves unknown packet bytes/semantics.
- `auditing-cross-server-flows` if the failure boundary spans Login, Char, Map, or World.
- `triangulating-ro-evidence` if trusted sources disagree.

## Hand off to

- `verifying-iro-protocol` once the suspected protocol behavior is understood.
- `implementing-athena-features` once root cause and required behavior are established.
- `writing-regression-tests` after the fix is proven.


## Goal

Find the **first meaningful divergence** between a known-good/expected flow and the failing flow, then test one evidence-based hypothesis at a time.

Do not shotgun-edit packets.

## First establish the failure boundary

Record:

- exact user action;
- last known-good client-visible behavior;
- last known-good inbound packet/event;
- outbound server bytes/events after it;
- first missing, malformed, rejected, or unexpected event;
- whether the TCP connection closes;
- whether the server throws/logs an error;
- whether the client crashes, hangs, reconnects, or returns to a previous screen.

Use the complete TCP stream around the failure. TCP segmentation is not packet framing.

## Read current project evidence

Before changing code:

1. `ai/iro-2026-wire.md`
2. the workstream doc that owns the failing boundary
3. current parser/serializer/handler
4. current regression tests
5. `git status` and `git diff`

## Classify the likely failure class

Keep competing hypotheses separate:

- framing/length mismatch;
- field offset/type/endian mismatch;
- wrong response order;
- missing required state burst;
- invalid authoritative game state;
- stale auth/session/handoff state;
- map/resource/name mismatch;
- timing/deferred-event bug;
- persistence failure;
- inter-server failure;
- observer/visibility inconsistency;
- client expectation still unknown.

## Compare against a known-good flow

Build a timeline:

`state -> inbound packet -> validation -> authoritative mutation -> outbound packet(s) -> next client event`

Compare failing and successful sessions at each point.

Do not jump directly to the last visible crash symptom if an earlier divergence exists.

## Reference use

Use rAthena to understand mature server semantics and state prerequisites.

Use OpenKore to interpret packet names/layout clues.

Use eAthena only for historical lineage.

If any reference conflicts with a verified stock-iRO capture, the capture wins.

## Experiment discipline

For each experiment:

1. write the hypothesis;
2. state the predicted result;
3. make the smallest change/observation that tests it;
4. run only the necessary flow;
5. record result;
6. revert diagnostic-only changes if disproven.

Avoid changing multiple packet fields or behaviors in one experiment.

## Safety rules

Never:

- patch Ragexe to hide a server bug;
- weaken ownership/auth validation merely to progress;
- hard-code values copied from one capture as production state;
- log or commit auth tokens/opaque sensitive payloads;
- treat a client crash as proof that the immediately preceding packet alone is wrong.

A client may receive an entire state burst before failing.

## Output

For substantial or reusable work, use [DEBUG-LEDGER.md](DEBUG-LEDGER.md). For a small focused task, preserve the same reasoning structure but report directly unless a persistent artifact is useful.

Once root cause is proven:
- add a regression test first or with the fix;
- update protocol documentation if a wire assumption changed;
- run independent protocol/reference coverage verification.
