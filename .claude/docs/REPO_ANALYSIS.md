# Repository analysis used to design the v2 bundle

Analysis date: 2026-10-04

## Instruction architecture decision

Claude Code now natively supports `AGENTS.md` in current versions, but the repository currently also has a `CLAUDE.md` that instructs Claude in natural language to read `AGENTS.md`. The v2 bundle removes that manual read indirection for Claude. It provides a compact Claude-specific `CLAUDE.md`, path-scoped `.claude/rules/`, task-scoped skills, and deterministic hooks. The existing repository `AGENTS.md` is intentionally not overwritten so it can remain useful to Codex/other agents.

The goal is not to reduce the number of skills arbitrarily. It is to minimize irrelevant context and make automatic skill selection precise.

## Repository identity

Analyzed repository: `PallasSoftware/pAthena`, project name Athena.NET.

Athena.NET describes itself as a modern C#/.NET Ragnarok Online server for the official unmodified International Ragnarok Online client. It explicitly says it is not a port of rAthena.


## Reference repositories

`.gitmodules` currently defines:

- `legacy/rathena` -> `rathena/rathena`
- `legacy/openkore` -> `OpenKore/openkore`

At analysis time the parent repository gitlinks were:

- rAthena: `e985006171d2eb320ee512a653f4c83aea3d81b6`
- OpenKore: `51de1ddfc4449ae5217f6886de702f87ca934030`

Skills must not rely on those literal SHAs forever. They instruct Claude to read the current gitlink/submodule status.

### eAthena

eAthena is not a configured submodule in Athena.NET.

The public `eathena/eathena` mirror exists and contains the historical Login/Char/Map/Common C server lineage. Its default branch was last pushed in 2018. It is useful only when lineage answers a question that rAthena/OpenKore/current iRO evidence cannot answer.

Role assigned in the Skills: `HISTORICAL`, never current iRO authority.

## Athena.NET solution and boundaries

The solution includes the primary LoginServer, CharServer, MapServer, WorldServer contracts/runtime, tests, WorldDataImporter, and launcher tooling.

Important current structural observations:

- .NET 10 is used.
- MapServer uses Orleans client contracts and references World contracts/runtime.
- World uses Orleans server packages.
- World/Map comments define explicit authority seams for mutable monster/world state.
- Some domain/runtime source is linked between projects, so an agent must inspect `.csproj` references before moving or duplicating types.
- The architecture separates client protocol projection from authoritative game state.

This led to a dedicated `designing-athena-features` skill and a cross-server/authority audit.

## Authoritative state

`AGENTS.md` and current World contracts repeatedly establish:

- client data is intent, never authoritative gameplay truth;
- server-side state must be resolved and validated;
- durable mutations should normally follow validate -> persist -> authoritative runtime update -> notify;
- one authoritative owner should exist for mutable state;
- stale identities/reconnects and duplicate requests need explicit handling.

These are therefore review/audit gates in multiple Skills.

## Protocol evidence model

`ai/README.md` defines a strong evidence order:

1. successful official stock-iRO captures;
2. repeatable official-client runtime behavior;
3. capture-derived Athena.NET tests;
4. current matching iRO community evidence;
5. rAthena/OpenKore references;
6. explicit hypotheses.

The Skills preserve this hierarchy.

This is why `triangulating-ro-evidence`, `verifying-iro-protocol`, and `reverse-engineering-iro-packets` are separate instead of forcing all protocol work into the rAthena investigation skill.

## rAthena structure relevant to research

Pinned rAthena has the expected major trees:

- `src/login`
- `src/char`
- `src/map`
- `src/common`
- `db`
- `npc`
- `conf`
- `sql-files`

The Map source includes large mature subsystems such as:

- `clif.cpp/.hpp`
- packet registration/shuffle tables
- battle
- pc
- status
- skill
- unit
- mob
- npc/script
- itemdb
- chrif/intif
- guild/party/instance/etc.

A correct research workflow therefore cannot stop at one handler. It must combine symbol navigation with searches for packet IDs, macros, config, data, scripts, inter-server calls, timers, and cleanup.

## OpenKore structure relevant to protocol research

Pinned OpenKore includes:

- `src/Network/PacketParser.pm`
- `src/Network/MessageTokenizer.pm`
- `src/Network/Receive.pm` plus regional receive modules
- `src/Network/Send.pm` plus send modules
- `src/Network/Receive/iRO*`
- packet/server tables

This is valuable for packet names, client-side interpretation, field layouts, and regional iRO clues.

It is not authoritative server-semantic evidence.

## Generated world-content architecture

This is one of the most important repo-specific findings.

`src/MapServer/Generated/README.md` explicitly says:

- generated files are normal compiled C#;
- do not edit them by hand;
- fix compiler/source selection and regenerate;
- definitions, placements, behavior, and runtime identity are different concepts;
- rAthena `duplicate(...)` declarations can share one definition/script but have independent placements/runtime actors.

`WorldDataImporter` implements a source -> lexer/parser -> semantic analysis -> lowering -> deterministic C# pipeline.

The repository documents a real previous bug where generic/base placements in a duplicate family were excluded while numbered duplicates were emitted, leaving a valid tutorial-map variant incomplete.

That history directly motivated the `maintaining-generated-world-content` skill and its explicit source-coverage checklist.

## Why the Skill set is split this way

The recurring failure mode being prevented has several distinct causes:

1. incomplete reference discovery;
2. conflicting evidence;
3. correct reference behavior mapped into the wrong Athena.NET boundary;
4. incomplete implementation;
5. implementation that passes its own tests but omitted a secondary source path;
6. wire assumptions not actually proven by iRO;
7. generated source coverage errors;
8. cross-server/persistence authority errors.

A single giant “port rAthena” skill would mix these stages, encourage early coding, and consume more context.

The bundle therefore uses small triggering Skills with explicit handoff gates.

## Names intentionally use gerund form

Current Anthropic Skill authoring guidance recommends concise, specific skill names/descriptions, often using gerund-form names, and progressive disclosure through supporting files.

The bundle follows that approach.

## Key non-goals

The Skills must never turn Athena.NET into:

- a generic rAthena/kRO compatibility server;
- a mechanical C/C++ port;
- a client-patched server;
- a system where OpenKore packet tables outrank official-client evidence;
- a system where generated files are maintained by hand;
- a distributed system with duplicated authoritative state merely for convenience.
