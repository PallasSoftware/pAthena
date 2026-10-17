# rAthena reference runner (development only)

Executes real pinned rAthena (`e985006171d2eb320ee512a653f4c83aea3d81b6`) and writes the golden fixture
`tests/MapServer.Tests/Gameplay/Reference/rathena-reference.json`. Not a runtime dependency of Athena.NET.

## How it works
`Dockerfile` builds the unmodified pinned `map-server` (Renewal default) from a `git archive` copy
(`legacy/rathena` is never touched). The only change is one call to `ra_reference_run()` after map-server's
normal init (all `db/re` YAML, battle config, map cache and 3000+ NPCs loaded). `reference_runner.cpp` builds
inputs and calls real `status_calc_pc`, `status_change_start` and the `mob_db` loader; it contains no formulas.
A throw-away MariaDB in the container satisfies map-server's SQL init.

## Regenerate / verify
Requires PowerShell 7 (`pwsh`), Docker, git and tar (macOS, Linux or Windows).

```
pwsh tools/rathena-reference/run.ps1                 # rewrites the committed fixture
git diff --exit-code tests/MapServer.Tests/Gameplay/Reference/rathena-reference.json   # must be empty: output is deterministic
dotnet test tests/MapServer.Tests --filter RathenaReferenceFixtureTests
```
Regeneration is deliberate only; .NET tests never write the fixture.

## Scenarios
- VF-01 fresh Novice (Lv1/Job1, stats 1, unarmed, prontera): full base/battle status.
- VF-07 Blessing 10→3, 10→10, 3→10 via `status_change_start` (timers/deadlines not captured).
- VF-14 mob 1002 values as stored by the real loader only (attack roll bounds are NOT yet captured; `battle_calc_base_damage` is `static`).

## Known gaps
Athena.NET overwrites Blessing 10 with 3 (reference keeps 10) → skipped test, Epic 15.2.
