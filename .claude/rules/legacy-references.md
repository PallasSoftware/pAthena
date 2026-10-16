---
paths:
  - "legacy/rathena/**"
  - "legacy/openkore/**"
---

# Pinned reference repositories

- These trees are pinned reference dependencies and are read-only during normal Athena.NET work.
- rAthena is primarily a server/gameplay/architecture/data reference. OpenKore is primarily a packet naming, client interpretation, and iRO/community clue source.
- Neither overrides verified stock-iRO evidence for client-facing behavior.
- Do not run `git pull`, `git checkout` to a branch, `git reset`, `git clean`, or `git submodule update --remote` as routine work.
- Updating a gitlink is deliberate dependency maintenance and should be an explicit task/commit.
- Historical eAthena is not a local submodule and is never current iRO authority.
