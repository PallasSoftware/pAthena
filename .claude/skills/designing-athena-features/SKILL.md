---
name: designing-athena-features
description: Designs non-trivial Athena.NET features after required behavior is understood and before coding. Use when state ownership, server/World boundaries, persistence placement, protocol adapters, generated-content ownership, Orleans contracts, or new abstractions require a decision. Do not use for small changes that already fit an established pattern or when behavior itself is still materially unknown.
---

# Designing Athena.NET Features

## Use when

Use when a non-trivial implementation still requires an architectural decision about ownership, boundaries, contracts, persistence, generated content, or abstractions.

## Do not use when

Do not use for a small change that clearly follows an existing pattern, or while material behavior/protocol facts are still unresolved.

## Escalate to

- `investigating-rathena` when required gameplay/reference behavior is not yet understood.
- `triangulating-ro-evidence` or `reverse-engineering-iro-packets` when wire evidence remains material and uncertain.

## Hand off to

- `implementing-athena-features` once the smallest design and authoritative owner are explicit.
- `auditing-cross-server-flows` if the proposed design spans distributed boundaries.


## Purpose

Translate verified behavior into an Athena.NET design without copying rAthena's architecture.

The design must fit the **current repository**, not an imagined future architecture and not a one-to-one C/C++ port.

## Inputs

Before designing:

1. Read only the task-relevant `ai/` documentation and the current architectural contracts involved.
2. Read the research brief when the feature is based on rAthena/reference behavior.
3. Resolve material wire uncertainties with `triangulating-ro-evidence` or `verifying-iro-protocol`.
4. Inspect the nearest current implementation and tests.
5. Inspect project references in the relevant `.csproj` files.
6. Inspect `git status` and `git diff`.

If the behavior itself is still unclear, stop designing and return to research.

## First question: who owns the truth?

For every mutable datum, name exactly one authoritative owner.

Examples of questions to answer:

- Is this account/character persistence owned by CharServer?
- Is active world/monster state owned by World?
- Is this MapServer-local scheduling or packet projection state?
- Is it immutable generated/reference content?
- Is it session-local presentation state?

Do not introduce duplicate authoritative state without an explicit synchronization contract.

For monster/world work, inspect current `Athena.World.Contracts` and implementation. Existing authority comments and contracts take precedence over remembered designs.

## Boundary rules

### Client protocol boundary

Packet IDs, field offsets, packet-version quirks, and wire serialization belong at the iRO-facing protocol edge.

Do not leak packet IDs/layout concerns into domain/world APIs merely because rAthena combines them.

### Domain/gameplay boundary

Gameplay rules should consume already-resolved authoritative state, not raw client claims.

Client packets express intent. Athena.NET validates and resolves the actual actor, item, slot, amount, target, skill, quest, map, and state server-side.

### Persistence boundary

For durable mutations, preserve the project rule:

`validate -> persist -> update authoritative runtime state -> notify client`

If a specific current path safely uses a different ordering, document the transaction/rollback/idempotency reason rather than silently normalizing it.

### Distributed boundary

Do not create Orleans grains merely because an object exists.

Prefer existing coarse consistency boundaries and current contracts. A network/distributed call is not a free abstraction.

### Generated-content boundary

If the requested change belongs to `src/MapServer/Generated`, do not design a hand edit. Route through the relevant importer/compiler/parser/lowering/emitter and regeneration flow.

## Repository pattern search

Before inventing a new abstraction:

1. Find the closest existing feature.
2. Follow references to understand its real boundary.
3. Check tests to learn what is intentionally stable.
4. Check whether a suitable service/contract/value object already exists.
5. Check source-linked files and project references before moving or duplicating types.
6. Check generated content before adding a handwritten duplicate.
7. Prefer extending a proven pattern over introducing a parallel architecture.

## Design checklist

The design must specify:

- feature scope and explicit non-goals;
- verified required behavior;
- authoritative owner for each mutable state;
- read/write data flow;
- protocol boundary responsibilities;
- persistence responsibilities;
- inter-server/distributed calls;
- concurrency and idempotency strategy;
- failure semantics;
- disconnect/stale-state semantics where relevant;
- generated versus handwritten ownership;
- likely files/projects touched;
- migrations/config changes, if any;
- test strategy;
- observability/logging needs without secrets;
- unresolved assumptions.

## Over-engineering gate

Before adding each interface, manager, coordinator, factory, event, grain, queue, or cache, answer:

1. What current requirement needs it?
2. What existing abstraction cannot satisfy that requirement?
3. What invariant becomes clearer or safer because it exists?

If those answers are weak, do not add the abstraction.

Minimize implementation complexity, **not required behavior**.

## Output

For substantial or reusable work, use [DESIGN-BRIEF.md](DESIGN-BRIEF.md). For a small focused task, preserve the same reasoning structure but report directly unless a persistent artifact is useful.

Do not write production code while producing the design unless the task explicitly combines design and implementation.
