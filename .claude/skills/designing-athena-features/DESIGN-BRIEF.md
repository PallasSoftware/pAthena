# Athena.NET Design Brief

## Scope
- Feature:
- Required behavior:
- Non-goals:

## Existing pattern
Nearest current Athena.NET implementation and why it is relevant.

## Authority matrix
| State | Authoritative owner | Readers | Writers | Persistence |
|---|---|---|---|---|

## Data / control flow
Describe the ordered flow from request to authoritative result to client projection.

## Boundaries
- iRO protocol edge:
- MapServer:
- CharServer:
- LoginServer:
- World/Orleans:
- generated/static content:

## Failure semantics
Validation, stale state, retries, duplicates, disconnects, downstream failure.

## Files/contracts likely touched
List existing files first, new files second.

## New abstractions
For each proposed abstraction, state why an existing one is insufficient.

## Testing plan
| Invariant | Lowest useful test layer | Test project |
|---|---|---|

## Unknowns / decisions deferred
