# Athena.NET Claude Code agent bundle v2

This bundle is designed for `PallasSoftware/pAthena` (Athena.NET). It turns the existing project-specific skills into a layered Claude Code setup with minimal always-on context and explicit routing.

## Design model

```text
CLAUDE.md          = always know
.claude/rules/     = know when touching this area
.claude/skills/    = know how to perform this task
hooks              = cannot violate this directly
ai/                = consult evidence when needed
LSP / Graphify     = inspect current implementation/reference truth
captures / tests   = prove behavior
```

The routing principle is:

> Select the smallest sufficient skill set. Escalate only when new evidence creates a reason.

## Install

Extract this ZIP into the **Athena.NET repository root**. Review the included `CLAUDE.md` before replacing your current one, then commit the files you want to share with the team.

The bundle contains:

```text
CLAUDE.md
.claude/
├── settings.json
├── hooks/
│   └── protect-repository-paths.py
├── rules/
│   ├── generated-content.md
│   ├── legacy-references.md
│   ├── persistence.md
│   ├── protocol-code.md
│   └── world-authority.md
└── skills/
    ├── auditing-cross-server-flows/
    ├── auditing-persistence-mutations/
    ├── debugging-stock-client-failures/
    ├── designing-athena-features/
    ├── implementing-athena-features/
    ├── investigating-rathena/
    ├── maintaining-generated-world-content/
    ├── reverse-engineering-iro-packets/
    ├── reviewing-athena-changes/
    ├── triangulating-ro-evidence/
    ├── verifying-iro-protocol/
    ├── verifying-reference-coverage/
    └── writing-regression-tests/
```

## AGENTS.md

Current Claude Code versions support `AGENTS.md` natively. This bundle does **not** overwrite the repository's existing `AGENTS.md`, because it may remain useful to Codex and other coding agents.

The included `CLAUDE.md` no longer tells Claude in natural language to manually read `AGENTS.md`. With the normal Claude Code project-instruction behavior, `CLAUDE.md` can be the compact Claude-specific always-on layer while `AGENTS.md` remains available to other agents. Avoid configuring Claude to load both large files if they duplicate the same invariants.

## Skill routing

Every `SKILL.md` now has four explicit routing sections:

- `Use when`
- `Do not use when`
- `Escalate to`
- `Hand off to`

This is deliberate. Skill descriptions are always visible to Claude for automatic selection, so routing boundaries should be sharper than the full workflow body.

### Typical feature

```text
investigating-rathena            only if reference behavior is not established
        ↓
triangulating-ro-evidence        only if evidence conflicts
        ↓
designing-athena-features       only if a non-trivial design decision remains
        ↓
implementing-athena-features
        ↓
verifying-reference-coverage     for substantial reference-derived behavior
verifying-iro-protocol           only when client wire changed
        ↓
writing-regression-tests         when a meaningful invariant needs protection
        ↓
reviewing-athena-changes         for substantial final review
```

Do not invoke the entire chain automatically.

### Unknown packet

```text
reverse-engineering-iro-packets
        ↓
verifying-iro-protocol
        ↓
implementing-athena-features
```

### Client crash/hang/disconnect

```text
debugging-stock-client-failures
        ↓
(add only the specialized skill the evidence requires)
```

## Independent coverage audit

`verifying-reference-coverage` is intentionally expectation-first:

```text
requirement
  ↓
stock-iRO/project evidence
  ↓
reference-source investigation
  ↓
expected-behavior matrix
  ↓
ONLY THEN inspect implementation in detail
  ↓
coverage comparison
```

This reduces confirmation bias and targets the recurring failure mode where an implementation looks complete until a second rAthena caller, helper side effect, timer, persistence path, or source variant is discovered later.

## Path-scoped rules

Project invariants that only matter in specific areas live under `.claude/rules/` rather than being copied into many skills. Rules use `paths:` frontmatter so Claude loads them when it reads/writes/edits matching files.

## Hooks

`.claude/settings.json` registers a `PreToolUse` hook. The hook:

- blocks direct `Edit`/`Write` under `src/MapServer/Generated/**`;
- blocks direct `Edit`/`Write` under `legacy/rathena/**` and `legacy/openkore/**`;
- blocks Bash mutations that reference those protected paths unless clearly read-only;
- allows the approved `WorldDataImporter` invocation to regenerate generated output;
- blocks routine `git submodule update --remote`.

The hook intentionally does not treat every Bash command as safe. Read-only inspection is allowed, and generation is allowed through the owning importer. Direct mutation is denied.

The hook uses `python3`. Verify it on each development platform before relying on it as a team policy.

## Templates

Skill templates such as `RESEARCH-BRIEF.md` and `COVERAGE-MATRIX.md` are no longer mandatory for tiny questions. Skills instruct Claude to use them for substantial/reusable investigations and to report focused results directly when a persistent artifact would add no value.

## Validation

Run:

```bash
python3 .claude/tools/validate_agent_skills.py
python3 .claude/tools/test_repository_guardrails.py
```

The validator checks structure, routing boundaries, line limits, stale skill names, overly broad preload instructions, and duplicate names. The hook test verifies the main allow/deny cases.

## Repository assumptions used by this bundle

- Athena.NET targets the official unmodified stock iRO client.
- `legacy/rathena` and `legacy/openkore` are pinned submodules and reference material.
- eAthena is historical external lineage, not a local submodule or iRO authority.
- Generated C# is owned by WorldDataImporter/compiler/source-selection logic.
- Current architecture includes LoginServer, CharServer, MapServer, World/Orleans boundaries, SQL Server persistence, and evidence-driven protocol support.
