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

Graphify is an optional discovery accelerator, not evidence. Generated graph output (`graphify-out/`, `graphify-out-rathena-epic15/`) is gitignored local state: never commit it.

- When the CLI and a relevant local graph exist, `graphify query "<question>"` (or `path` / `explain`) is a cheap way to locate files before broad grep. Use the Athena.NET graph for Athena.NET code and the separate pinned-rAthena graph for `legacy/rathena`; do not mix scopes.
- When Graphify is unavailable, stale, incomplete, or irrelevant to the question, use LSP or direct source search. Do not rebuild or refresh the graph just to proceed.
- A missing graph node or edge is never proof that source behavior is absent. Read the source.
- Rebuilding (`graphify update .`) is a local, optional step when you want a fresher graph; it is not required after edits.
- Read `GRAPH_REPORT.md` only for broad architecture review.
