# Migration notes from v1

## What changed

1. `implementing-rathena-features` was renamed to `implementing-athena-features`.
2. Every skill now defines `Use when`, `Do not use when`, `Escalate to`, and `Hand off to` boundaries.
3. `verifying-reference-coverage` is expectation-first: build the source/evidence-derived behavior matrix before detailed implementation inspection.
4. Broad `Read AGENTS.md` / `Read ai/README.md` preloads were removed from skills. Skills now request only task-relevant evidence.
5. Universal Claude context moved into a compact root `CLAUDE.md`.
6. Area-specific constraints moved into path-scoped `.claude/rules/`.
7. Direct writes to generated and pinned legacy trees are protected by a `PreToolUse` hook registered in `.claude/settings.json`.
8. Templates are proportional: substantial investigations use persistent templates; small focused questions can report directly.
9. The validator now checks routing boundaries, stale names, structure, settings, path rules, and excessive size.
10. Evaluation scenarios now include negative routing, minimal-composition, and over-triggering tests.
11. Bundle documentation is English.

## AGENTS.md note

Current Claude Code releases support `AGENTS.md` natively. This bundle does not delete or replace Athena.NET's existing `AGENTS.md`. The bundled `CLAUDE.md` is intended to be the concise Claude-specific always-on context, while `AGENTS.md` may remain useful to Codex and other agents.

The old pattern where `CLAUDE.md` merely said "read AGENTS.md" is removed because that was indirect and made context ownership unclear.

## Hook rollout

Before committing the hook for the whole team:

```bash
python3 .claude/tools/test_repository_guardrails.py
```

Then start Claude Code and run `/hooks` to confirm the project `PreToolUse` hook is registered.

The hook intentionally requires `python3`. If a supported developer platform does not provide that command, replace the implementation with an equivalent project-standard runtime before treating the hook as mandatory team policy.
