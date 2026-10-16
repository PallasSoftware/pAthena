# Athena.NET Claude Code instructions

Athena.NET is a modern C#/.NET Ragnarok Online server for the official, unmodified stock iRO client.

## Always true

- Stock-iRO compatibility is the client target. Do not add generic kRO/rAthena compatibility solely for parity.
- Do not patch or modify Ragexe to compensate for server incompatibilities.
- Verified stock-iRO captures, repeatable stock-client behavior, and capture-derived Athena.NET tests outrank legacy packet assumptions.
- `legacy/rathena/` and `legacy/openkore/` are pinned reference material, not architecture or wire authorities to copy blindly.
- Treat client-provided gameplay data as intent. Resolve and validate authoritative server-side state before mutation.
- Mutable runtime state must have one explicit authoritative owner. Do not introduce duplicated authority across MapServer, World/Orleans, or persistence.
- For durable mutations, preserve validation, failure, retry/idempotency, and persistence semantics before client success projection.
- Microsoft SQL Server is the persistence platform.
- Prefer the current repository architecture and the smallest correct change over new abstractions or unrelated refactors.
- Never commit or log passwords, PINs, bearer/JWT/session/auth tokens, service tokens, or unsanitized sensitive captures.
- Inspect current code, tests, configuration, `git status`, and `git diff` before substantial changes. Preserve compatible user changes.

## Evidence and documentation

Read only documentation relevant to the task. `ai/iro-2026-wire.md` is required for material client-wire claims. Server/workstream documents under `ai/` are evidence and status records, not mandatory preload for unrelated work.

## Skills

Select the smallest sufficient skill set. Escalate only when new evidence creates a reason. Do not invoke research, protocol, design, review, or audit skills merely because they exist.

Project skills live under `.claude/skills/`. Their `Use when`, `Do not use when`, `Escalate to`, and `Hand off to` sections define routing boundaries.

## Rules and enforcement

Path-scoped constraints live under `.claude/rules/`. Deterministic write protections are enforced by project hooks configured in `.claude/settings.json`.

## Verification

Run focused tests first, then the owning test project/build. For WorldDataImporter changes, also run `git diff --check` after regeneration. A green build is necessary but does not replace evidence/coverage verification when a task changes protocol or reference-derived behavior.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- When graphify-out/graph.json exists, run `graphify query "<question>"` before your first grep, search or source-file read for a codebase question, and put this rule in every subagent prompt that explores code. Read raw files only for lines the graph or an earlier read already located. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
- A committed graphify-out/graph.json is rewritten whole on every build, so a large diff to it in a pull request is expected. Do not flag it as churn or suggest skipping the graph rebuild or its hooks.
