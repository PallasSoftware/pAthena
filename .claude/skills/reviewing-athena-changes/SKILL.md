---
name: reviewing-athena-changes
description: Reviews actual Athena.NET code changes for correctness, safety, maintainability, authority boundaries, evidence use, persistence safety, distributed state, generated-content rules, and regression coverage. Use when reviewing a substantial diff or another agent’s completed work. Do not use as the primary workflow for independently discovering expected reference behavior; use verifying-reference-coverage for that.
---

# Reviewing Athena.NET Changes

## Use when

Use when there is an actual implementation/diff to review for correctness, scope, safety, architecture fit, tests, and maintainability.

## Do not use when

Do not use as the primary method to determine what behavior should exist. For an independent “what did we miss?” audit, use `verifying-reference-coverage` first.

## Escalate to

- `verifying-reference-coverage` if expected behavior is not independently established.
- `verifying-iro-protocol` if protocol correctness is uncertain.
- `auditing-persistence-mutations` or `auditing-cross-server-flows` for high-risk specialized findings.

## Hand off to

- `implementing-athena-features` only if the task explicitly includes fixing review findings.
- `writing-regression-tests` when a finding needs permanent protection.


## Review posture

Treat the diff as a candidate implementation, not as the specification.

Read the underlying requirement, the changed code/tests, and only the task-relevant `ai/` evidence before judging the implementation.

## Review order

### 1. Correctness against requirement/evidence

Ask:
- Is every required behavior present?
- Did lower-authority legacy behavior override verified iRO facts?
- Are unknown wire semantics being presented as proven?

For reference-derived features, invoke the logic of `verifying-reference-coverage`.

### 2. Authority and trust

Check:
- client input remains untrusted intent;
- one authoritative owner exists for mutable state;
- MapServer/World boundaries are preserved;
- stale/replayed operations cannot mutate a replacement entity;
- success is not projected before required authoritative/durable success.

### 3. Protocol correctness

When wire code changed, verify:
- lengths/offsets/order;
- framing;
- state sequence;
- capture/test provenance.

### 4. Persistence safety

Check validation, transaction/order, failure handling, retries, duplicate requests, and migrations.

### 5. Generated content

Reject hand-edited generated files unless the task is specifically regenerating output from a generator change.

Verify source selection/provenance and deterministic generation.

### 6. Architecture fit

Check for:
- unnecessary interfaces/factories/coordinators;
- duplicated domain logic;
- packet knowledge leaking into domain/world contracts;
- accidental project-reference cycles;
- duplicated linked-source types;
- new distributed calls on a hot path without need.

### 7. Tests

Ask whether tests prove the invariant or merely mirror the implementation.

Look for missing negative, stale, duplicate, fragmented/coalesced, and source-coverage cases.

## Findings format

Order findings by severity:

- `CRITICAL`
- `HIGH`
- `MEDIUM`
- `LOW`

Each finding must contain:
- path/symbol;
- concrete failure mode;
- why it matters;
- evidence/invariant violated;
- smallest safe correction.

Do not bury significant findings in a prose summary.

If no material finding exists, say so and list residual risks/untested areas.

## Scope

Do not rewrite the feature during review unless explicitly asked. Review first; remediation can be a separate step.
