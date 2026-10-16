# Cross-Server Flow Audit

## Flow
Example: Login -> Char -> Map -> World.

## Sequence
| # | Caller | Callee | Contract/event | Identity/auth | Mutation | Reply |
|---:|---|---|---|---|---|---|

## Authority matrix
| State | Authoritative owner | Lifetime | Stale/replay protection |
|---|---|---|---|

## Failure paths
| Failure | Expected behavior | Cleanup/retry/idempotency |
|---|---|---|

## Trust boundaries
Separate client auth/session proof from inter-service authentication.

## Findings
Severity-ordered findings.

## Verdict
