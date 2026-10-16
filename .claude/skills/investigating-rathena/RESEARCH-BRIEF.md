# rAthena Research Brief

## Feature
- Requested behavior:
- Athena.NET target area:
- Pinned rAthena commit:
- Research date:

## Project evidence read
- Applicable project/rule invariants:
- `ai/...`:
- existing Athena.NET tests/code:

## Entry points
| Source | Symbol / declaration | Role | Evidence class |
|---|---|---|---|

## Complete behavior trace
Write the ordered success path.

## Validation and failure paths
| Condition | Source symbol | Result / side effect |
|---|---|---|

## State reads and mutations
| State | Owner in rAthena | Read/write | When |
|---|---|---|---|

## Persistence
| Operation | Source | Durable data | Ordering / failure behavior |
|---|---|---|---|

## Inter-server behavior
| Caller | Callee | Message/contract | State effect |
|---|---|---|---|

## Client-visible behavior
| Packet / event | Direction | Trigger | Evidence status |
|---|---|---|---|

## Deferred behavior
Timers, callbacks, scheduled work, continuation state, cleanup.

## Conditions
`RENEWAL`, `PACKETVER`, regional, config, script/data conditions.

## Completeness search
- alternate callers checked:
- helpers with side effects checked:
- cleanup paths checked:
- script/data dependencies checked:
- duplicate/template families checked:

## Athena.NET relevance
| Reference behavior | Classification | Should Athena.NET implement it? | Why |
|---|---|---|---|

## Unknowns
List facts still `UNCERTAIN`.

## Research closure
- [ ] entry point known
- [ ] callers/callees traced
- [ ] success + failure paths traced
- [ ] state/persistence traced
- [ ] inter-server effects traced or absent
- [ ] client-visible effects listed
- [ ] deferred effects traced or absent
- [ ] relevant conditions checked
- [ ] unknowns explicit
