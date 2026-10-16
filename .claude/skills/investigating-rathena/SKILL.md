---
name: investigating-rathena
description: Investigates relevant behavior in the pinned rAthena reference before Athena.NET implementation. Use when a task depends on understanding rAthena gameplay, validation, timers, scripts, persistence, packet-side effects, callers, or cross-server behavior. Do not use when authoritative project/iRO evidence already answers the question or when the task is unrelated to rAthena behavior.
---

# Investigating rAthena

## Use when

Use when Athena.NET work depends on understanding how pinned rAthena implements a gameplay/server behavior, including call chains, side effects, validation, timers, persistence, scripts, data, or failure paths.

## Do not use when

Do not use when the behavior is already established by current Athena.NET/iRO evidence, when only implementation remains, or when the task has no meaningful rAthena reference dependency.

## Escalate to

- `triangulating-ro-evidence` if rAthena conflicts with stock-iRO evidence, OpenKore, or current project behavior.
- `reverse-engineering-iro-packets` if the blocking question is an unknown client packet/field rather than server semantics.

## Hand off to

- `designing-athena-features` for non-trivial architecture decisions.
- `implementing-athena-features` when behavior is complete and architecture is already obvious.
- `verifying-reference-coverage` after implementation of a feature-level (full-trace) investigation, for a fresh omission audit.


## Purpose

Produce a source-backed behavior model before Athena.NET code is changed.

This is a **research gate**. Do not start implementing the requested Athena.NET feature until the investigation has reached the closure criteria below and a research brief exists.

## Mandatory preflight

1. Read `ai/iro-2026-wire.md` when the feature can affect client-facing behavior.
2. Read the relevant `ai/login-server.md`, `ai/char-server.md`, `ai/map-server.md`, `ai/data-and-tools.md`, or `ai/world-data.md`.
3. Inspect the relevant Athena.NET code and tests so the investigation asks the right questions.
4. Inspect `git status` and `git diff`. Preserve unrelated user changes.
5. Confirm `legacy/rathena` is initialized at the commit pinned by the parent repository. Never update it to upstream `main` as part of ordinary research.

Do not hard-code a remembered rAthena SHA. Read the current gitlink or `git submodule status`.

## Source discovery strategy

Use multiple navigation methods because no single method is complete:

1. If the pinned-rAthena Graphify graph and CLI are available, use them to discover likely symbols, call paths, and neighboring files. Missing graph nodes are not evidence of absent behavior.
2. Use C/C++ language-server navigation for definitions, references, callers, callees, and types where available.
3. Use repository text search for:
   - packet IDs in hexadecimal and decimal form;
   - packet names;
   - function and struct names;
   - enum/constants;
   - script commands;
   - database fields/tables;
   - configuration flags;
   - `#if`, `#ifdef`, `PACKETVER`, `RENEWAL`, and regional guards.
4. Read the actual source around every critical match.

**Graph, LSP, and search results are discovery aids, not evidence by themselves. The source is the evidence.**

## rAthena source map

Start in the area matching the task, then expand outward.

### Client and packet behavior

Inspect as applicable:

- `legacy/rathena/src/map/clif.cpp`
- `legacy/rathena/src/map/clif.hpp`
- `legacy/rathena/src/map/clif_packetdb.hpp`
- `legacy/rathena/src/map/clif_shuffle.hpp`
- matching login/char client-interface files

Trace both packet registration and the handler/serializer implementation. Packet-table presence alone is not behavior.

### Gameplay and runtime mechanics

Search the relevant map subsystem, commonly including:

- `pc.*`
- `status.*`
- `skill.*`
- `battle.*`
- `unit.*`
- `mob.*`
- `npc.*`
- `script.*`
- `quest.*`
- `itemdb.*`
- party, guild, storage, achievement, instance, vending, or other domain files as relevant

### Cross-server behavior

Inspect:

- `chrif.*`
- `intif.*`
- `legacy/rathena/src/char/`
- `legacy/rathena/src/login/`

Do not stop at the MapServer side if the operation persists or coordinates through Char/Login.

### Data and scripts

Inspect as relevant:

- `legacy/rathena/db/`
- `legacy/rathena/npc/`
- `legacy/rathena/conf/`
- `legacy/rathena/sql-files/`

A script-driven or data-driven behavior may not be visible in the C++ call chain alone.

## Scale to the task

- **Narrow question** (one formula, constant, validation rule, or data field; no mutation or cross-server effect): trace only the items that bear on it, and state which items were skipped as not applicable. A short answer with exact source paths and symbols suffices; the full brief is not required.
- **Feature or mutating behavior** (state changes, persistence, packets emitted, timers, inter-server effects, or anything that will be implemented as a feature): the full trace and the research closure criteria below are mandatory.

When unsure which applies, treat it as a feature.

## Required trace

For the requested feature, identify all applicable items:

1. Entry points and dispatch registration.
2. Direct and indirect callers.
3. Callees that materially affect semantics.
4. Input validation and rejection paths.
5. Authentication, ownership, bounds, stale-state, and duplicate-request checks.
6. Authoritative state reads.
7. Authoritative state mutations.
8. Persistence reads/writes and transaction boundaries.
9. Inter-server requests, replies, and handoffs.
10. Client packets emitted, including error/negative replies.
11. Packet ordering dependencies.
12. Timers, deferred work, callbacks, scheduled events, and asynchronous continuations.
13. Idempotency or replay protection.
14. Disconnect/reconnect and stale-session behavior where relevant.
15. Compile-time and runtime configuration branches.
16. Renewal/pre-Renewal differences where they affect the selected behavior.
17. Script/data dependencies.
18. Side effects that are easy to miss because they occur in a helper.
19. Cleanup/rollback paths.
20. Tests or assertions in rAthena that clarify intended behavior, if present.

## Scope classification

For every discovered behavior, classify it as one of:

- `SERVER_MECHANIC` — useful server/gameplay semantics.
- `CLIENT_WIRE_REFERENCE` — packet-related reference that still requires iRO evidence.
- `PERSISTENCE_REFERENCE` — schema/state semantics useful to Athena.NET.
- `SCRIPT_OR_DATA` — behavior coming from source data or scripts.
- `LEGACY_OR_COMPAT` — compatibility behavior that Athena.NET should not inherit automatically.
- `UNCERTAIN` — not yet proven.

Do not translate `CLIENT_WIRE_REFERENCE` directly into Athena.NET packet code unless stock-iRO evidence supports it.

## Completeness traps

Explicitly hunt for these before declaring the investigation complete:

- a second handler for the same concept;
- a helper that performs hidden persistence or packet emission;
- success path traced but failure path skipped;
- behavior behind `RENEWAL`, `PACKETVER`, or regional guards;
- a timer/callback that performs the real state change later;
- a server-to-server call whose reply completes the behavior;
- a script command whose semantics live outside the parser;
- duplicate/template world-content declarations;
- shared cleanup code called from disconnect/death/map-change paths;
- state updates that happen before or after the obvious function;
- additional notifications to observers, party members, or nearby actors.

## Research closure criteria

For feature-level work, do not hand off to implementation until all are true:

- The entry point is known.
- Material callers/callees are traced.
- Success and failure paths are traced.
- State ownership and mutations are identified.
- Persistence is either traced or explicitly confirmed absent.
- Inter-server effects are either traced or explicitly confirmed absent.
- Client-visible effects are listed, with wire claims marked as reference-only unless independently verified.
- Timing/deferred effects are traced or confirmed absent.
- Conditional branches relevant to the target behavior are listed.
- Remaining unknowns are explicit rather than silently guessed.

## Output

Use [RESEARCH-BRIEF.md](RESEARCH-BRIEF.md) as the output shape.

For feature-level work the brief must include exact source paths and symbol names. Include line numbers when practical, but do not make line numbers the only locator because pinned-reference updates can move them.

Do not modify files under `legacy/rathena/`.
