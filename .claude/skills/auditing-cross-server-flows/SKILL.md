---
name: auditing-cross-server-flows
description: Audits Athena.NET behavior that crosses LoginServer, CharServer, MapServer, World/Orleans, service-authentication, transfer, or session-ownership boundaries. Use when a request, bug, retry, reconnect, handoff, or mutation spans two or more process/distributed boundaries. Do not use for work that is fully contained inside one server and has no cross-boundary identity, authority, or failure semantics.
---

# Auditing Cross-Server Flows

## Use when

Use when a flow crosses two or more server/process/distributed boundaries, especially login, character selection, map handoff, reconnect, service authentication, transfers, World calls, retries, or stale-session handling.

## Do not use when

Do not use for a change entirely inside one server when no cross-boundary identity, contract, retry, or ownership semantics are involved.

## Escalate to

- `auditing-persistence-mutations` if the cross-server flow also performs durable mutations.
- `verifying-iro-protocol` if a client-facing handoff or packet sequence must be checked.
- `debugging-stock-client-failures` if the flow is being investigated because the stock client stops progressing.

## Hand off to

- `implementing-athena-features` once the sequence, ownership, and failure semantics are established.
- `writing-regression-tests` for replay, stale-session, transfer, or failure-path invariants.


## Purpose

Trace a request across process/distributed boundaries while preserving identity, authority, idempotency, and failure semantics.

## Start with current contracts

Read:

- relevant Login/Char/Map docs
- `ai/iro-2026-wire.md` for client-facing handoffs
- current service contracts/connectors
- current World/Orleans contracts where involved

Do not infer the current architecture solely from older roadmap documents.

## Build the sequence

For each hop record:

1. caller/process;
2. callee/process;
3. transport/contract;
4. identity fields;
5. authentication proof;
6. state read;
7. validation;
8. state mutation;
9. persistence;
10. reply/result;
11. client projection;
12. retry/idempotency behavior;
13. disconnect/failure cleanup.

## Identity discipline

Keep distinct concepts distinct:

- account identity;
- character identity;
- login/session IDs;
- presence IDs;
- transfer IDs;
- monster life/incarnation identity;
- service identity;
- service-authentication tokens/proofs.

Do not substitute one identifier merely because values happen to match in a test.

## Trust boundaries

Service authentication and client-session authentication are different boundaries.

Never send/log shared service tokens when the current design uses derived proofs.

A successful upstream server authentication does not automatically prove client ownership of a requested character/action.

## State ownership

For every mutation, name the authoritative owner.

Particularly verify:

- no duplicate authoritative world state exists across MapServer and World;
- stale reconnects cannot mutate a replacement presence;
- transfer/handoff state is consumed or invalidated correctly;
- replayed requests are safe;
- server success is not announced before required durable work succeeds.

## Failure-path audit

Trace:

- callee unavailable;
- timeout;
- duplicate/retry;
- stale token/auth node;
- partial transfer;
- caller disconnect;
- target process restart;
- persistence failure;
- conflicting current ownership.

A happy-path sequence diagram is incomplete without these.

## Output

For substantial or reusable work, use [FLOW-MATRIX.md](FLOW-MATRIX.md). For a small focused task, preserve the same reasoning structure but report directly unless a persistent artifact is useful.

If the audit finds ambiguous authority, stop implementation until the ownership decision is explicit.
