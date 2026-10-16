---
paths:
  - "src/WorldServer/**"
  - "src/MapServer/World/**"
  - "tests/World.Tests/**"
  - "tests/MapServer.Tests/World/**"
---

# World and authority boundaries

- Every mutable gameplay datum has one authoritative owner. Do not maintain competing authoritative copies in MapServer and World/Orleans.
- Treat MapServer client packets as projection/adaptation, not authority for distributed world state.
- Preserve life/presence/epoch/idempotency identities where the existing contracts use them to reject stale mutations.
- Keep protocol packet IDs/layouts out of Orleans/game-domain contracts unless a proven boundary explicitly requires otherwise.
- Avoid per-object distributed grains merely because a Ragnarok object exists. Follow current coarse authority contracts and repository architecture.
- Use `auditing-cross-server-flows` for multi-boundary changes and `designing-athena-features` before changing ownership boundaries.
