---
paths:
  - "src/MapServer/Generated/**"
  - "tools/WorldDataImporter/**"
  - "tests/WorldDataImporter.Tests/**"
---

# Generated content

- `src/MapServer/Generated/**` is output, not an implementation target. Never hand-edit generated C#.
- Fix the owning source selection, lexer/parser, semantic conversion, lowering, emitter, or generator command and regenerate.
- Preserve deterministic output and source provenance from the currently pinned `legacy/rathena` gitlink.
- A `duplicate(...)` family must not silently lose its valid generic/base placement or other source variants.
- Unsupported source constructs must fail/report clearly rather than partially generating plausible output.
- For non-trivial work, use `maintaining-generated-world-content`.
