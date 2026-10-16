---
paths:
  - "src/LoginServer/Net/**"
  - "src/CharServer/Net/**"
  - "src/MapServer/Net/**"
  - "tests/LoginServer.Tests/**/Net/**"
  - "tests/CharServer.Tests/**/Net/**"
  - "tests/MapServer.Tests/**/Net/**"
  - "ai/iro-2026-wire.md"
---

# Stock-iRO protocol code

- Do not guess packet IDs, lengths, offsets, signedness, field ordering, encoding, or packet ordering.
- `ai/iro-2026-wire.md`, verified captures/runtime behavior, and capture-derived tests are higher authority than legacy packet tables.
- TCP read boundaries are not packet boundaries. Preserve fragmentation and coalescing correctness.
- Keep unknown/opaque fields unknown until evidence establishes semantics.
- Never log or commit sensitive opaque authentication material from captures.
- If expected wire behavior is unknown, use `reverse-engineering-iro-packets`; if known behavior needs code verification, use `verifying-iro-protocol`.
