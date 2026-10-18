# Custom item definitions

Athena.NET-authored content that sits on top of the pinned rAthena databases. `legacy/rathena` is never edited.

- `items/*.yml` — same schema as rAthena `db/re/item_db*.yml` (`Body:` sequence of item blocks).
  Passed to `WorldDataImporter compile-item-pack --custom-items data/custom/items`.

Files are read in ordinal file-name order after the pinned data and parsed by the same loader code:

- A block whose `Id` already exists overrides that definition. Only the keys written in the block change anything; every
  omitted key keeps the pinned value.
- Map fields (`Jobs`, `Classes`, `Locations`, `Flags`, `Delay`, `Stack`, `NoUse`, `Trade`) and scripts (`Script`,
  `EquipScript`, `UnEquipScript`) replace the pinned value wholesale when present. They are never merged or appended.
- A block with a new `Id` is a full definition and must carry `AegisName` and `Name`.
- An `Id` may appear only once across all custom files. A key the loader does not read fails generation.
