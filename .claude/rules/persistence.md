---
paths:
  - "src/CharServer/Db/**"
  - "src/CharServer/Migrations/**"
  - "src/**/*Persistence*.cs"
  - "src/**/*Repository*.cs"
  - "tests/**/*Persistence*.cs"
  - "tests/**/*Repository*.cs"
---

# Durable state

- Client values are intent, not authoritative state. Resolve ownership, entities, slots, amounts, and current values server-side.
- Preserve explicit validation and negative paths, including forged, stale, duplicate, out-of-range, and conflicting requests.
- Prefer `validate -> persist required durable state -> update authoritative runtime state in the project-defined order -> project success to the client` unless an existing atomic design proves another order correct.
- Do not invent schema solely to mirror a legacy column. Model Athena.NET requirements.
- Consider concurrency, uniqueness, foreign keys, retries/idempotency, partial failure, and disconnect timing.
- Use `auditing-persistence-mutations` for non-trivial mutation work.
