---
name: maintaining-generated-world-content
description: Maintains Athena.NET generated world/game content and the WorldDataImporter/compiler pipeline sourced from pinned rAthena. Use when generator source selection, parsing, semantics, lowering, emitters, provenance, duplicate placements, maps, NPCs, warps, skills/jobs/progression, or deterministic regeneration must change. Do not use to hand-edit generated C# or for unrelated runtime code.
---

# Maintaining Generated World Content

## Use when

Use when the owning change belongs in WorldDataImporter/compiler/source-selection/generation logic or when generated content must be regenerated from pinned sources.

## Do not use when

Do not use to directly patch `src/MapServer/Generated/**`, and do not use for ordinary runtime behavior unrelated to generated content.

## Escalate to

- `investigating-rathena` if the source semantics or complete duplicate/template behavior are unclear.
- `triangulating-ro-evidence` when generated protocol-facing data conflicts with stronger iRO evidence.

## Hand off to

- `writing-regression-tests` for compiler/source-coverage/determinism invariants.
- `verifying-reference-coverage` when source variants or duplicate families could have been omitted.


## Non-negotiable rule

**Do not hand-edit generated files to fix generated behavior.**

`src/MapServer/Generated` is output.

Fix the source selection, parser, semantic conversion, lowering, emitter, runtime contract, or generator, then regenerate.

## Read only the relevant generation sources

Start with:

1. `src/MapServer/Generated/README.md`;
2. `tools/WorldDataImporter/README.md`;
3. the relevant importer/compiler tests.

Read `ai/data-and-tools.md` and/or `ai/world-data.md` only when the task touches those documented architecture/data decisions.

## Provenance

Obtain the current pinned rAthena commit from the parent repository/submodule status.

Do not use a remembered SHA.

Any generator argument/provenance stamp that claims a rAthena commit must match the exact source tree read during generation.

Do not run `git submodule update --remote` as routine setup.

## Compiler pipeline

When output is wrong, inspect the earliest stage where meaning diverges:

`source loading -> lexer/parser -> syntax -> semantic analysis -> lowering -> deterministic C# emitter -> runtime`

Fix the earliest responsible stage.

Do not patch a later generated artifact to compensate for an earlier semantic bug.

## Source coverage

For rAthena scripts/data:

- trace template plus every relevant `duplicate(...)` member;
- do not assume numbered instances are “real” while the generic/base placement is disposable;
- inspect config/load graphs that determine active source files;
- preserve intentional first-match/layering rules;
- distinguish definition, placement, behavior, and runtime instance;
- ensure unsupported constructs are reported rather than partially compiled.

The repository has previously had real bugs from silently excluding generic/base placements. Treat source-selection completeness as a regression-critical invariant.

## Determinism

For the same:
- pinned source tree;
- generator version;
- selection arguments;

the generated output must be stable.

Do not inject local timestamps, machine paths, unordered iteration, or environment-dependent ordering into generated output.

## Runtime separation

Generated gameplay scripts should call controlled runtime APIs such as `ScriptContext`.

Do not leak:
- compiler AST types;
- parser internals;
- packet layout details;
- direct persistence implementation details

into generated script bodies unless the current architecture explicitly requires it.

## Verification

Use the exact documented generator command for the affected slice, adjusted only for the intended source selection.

Then run at minimum the relevant combination of:

```sh
dotnet test tests/WorldDataImporter.Tests/WorldDataImporter.Tests.csproj
dotnet test tests/MapServer.Tests/MapServer.Tests.csproj
dotnet build Athena.NET.sln -m:1
git diff --check
```

Also inspect the generated diff for:
- unexpected mass churn;
- missing placements;
- duplicate output;
- changed provenance;
- nondeterministic ordering.

## Output

For substantial or reusable work, use [GENERATION-CHECKLIST.md](GENERATION-CHECKLIST.md). For a small focused task, preserve the same reasoning structure but report directly unless a persistent artifact is useful.

If a source construct cannot be lowered correctly, report it as unsupported. Do not emit a silently incomplete behavior.
