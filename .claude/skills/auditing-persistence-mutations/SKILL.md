---
name: auditing-persistence-mutations
description: Audits durable Athena.NET gameplay/account mutations for authoritative-state resolution, validation, EF/SQL persistence, ordering, concurrency, retries, idempotency, and client projection. Use when inventory, items, equipment, quests, characters, currencies, stats, save state, or other durable data changes. Do not use for read-only queries or purely ephemeral runtime state with no durable mutation.
---

# Auditing Persistence Mutations

## Use when

Use when an operation creates, changes, consumes, deletes, or persists durable account/character/gameplay state.

## Do not use when

Do not use for read-only queries, serializers with no mutation, or purely ephemeral runtime state that never crosses a persistence boundary.

## Escalate to

- `auditing-cross-server-flows` if the mutation crosses server/World boundaries.
- `triangulating-ro-evidence` if reference behavior and project requirements disagree about mutation semantics.

## Hand off to

- `implementing-athena-features` once validation, authority, persistence ordering, and failure semantics are explicit.
- `writing-regression-tests` for forged, stale, duplicate, concurrency, and persistence-failure cases.


## Core rule

Client input is intent, not authoritative state.

Resolve all referenced entities and current values from Athena.NET's authoritative state before mutating anything.

## Preflight

Read the affected persistence service/repository, entity configuration/migrations, owning tests, and only the task-relevant `ai/` documentation.

Identify:

- durable owner;
- runtime owner;
- transaction boundary;
- caller retry behavior;
- client success/failure projection.

## Mutation trace

For each operation, trace:

1. authenticate session;
2. resolve account/character ownership;
3. resolve referenced server-side entity/state;
4. validate bounds/amount/slot/state/preconditions;
5. calculate result server-side;
6. persist required durable state;
7. handle concurrency/duplicate request;
8. update authoritative runtime state in the project-defined order;
9. notify/project result to client;
10. clean up/rollback on failure.

The project's preferred default is:

`validate -> persist -> update authoritative runtime state -> notify`

If current code intentionally differs, document why it is still atomic/safe.

## Specific abuse/edge cases

Test or reason through as applicable:

- forged item ID;
- stale slot/index;
- amount larger than owned;
- zero/negative/overflow amount;
- duplicate request/retry;
- already-completed quest;
- stale character/presence;
- concurrent mutation;
- DB failure;
- runtime update failure after durable success;
- disconnect between persistence and notification.

## EF/SQL checks

When schema/model changes:

- inspect migration impact;
- preserve existing data where required;
- consider uniqueness/foreign keys/concurrency;
- verify indexes for identity/ownership lookups if relevant;
- keep secrets/connection strings out of commits.

Do not create a migration just because a legacy schema has a column. Model the Athena.NET requirement.

## Output

For substantial or reusable work, use [MUTATION-LEDGER.md](MUTATION-LEDGER.md). For a small focused task, preserve the same reasoning structure but report directly unless a persistent artifact is useful.

Findings that can create/duplicate/delete authoritative state incorrectly are high severity even if the official client normally sends valid requests.
