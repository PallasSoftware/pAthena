# Epic 15 master plan (branch `feature/epic-15-reference-runner`, PR #47 — one branch, one PR, no merge without approval)

Approved execution plan for Epic 15 on branch `feature/epic-15-reference-runner` (PR #47). Status is tracked in the PR checklist and `renewal-status-matrix.md`.

## A. Verified baseline (today)
- Branch has 15.1: `tools/rathena-reference/{Dockerfile,run.ps1,reference_runner.cpp,README.md}`; fixture `tests/MapServer.Tests/Gameplay/Reference/rathena-reference.json` (schema 1, commit-pinned, all `ReferenceExecuted`); loader `RathenaReferenceFixture.cs`; tests `RathenaReferenceFixtureTests.cs`.
- **CI:** PR #47 `Validate` is now green after re-run (the earlier failure was `Attack_MovementRequest_CancelsActiveRepeatAttack`, world `NotFound` lifecycle rejection during shutdown; 6/6 local passes; it did not recur). Not proven unrelated — it is an intermittent test in the same area Stage 15.2/15.5 touches. Task T0 below was a bounded investigation (see "Movement-retarget race" at the end of this document); stabilize with an evidence-backed fix + regression test, no test weakening.
- **Active comparisons:** VF-01 only `battle_status.adelay` vs `AttackDelayCalculator` (matches). VF-07 sequences 3→10, 10→10 (val1/val2 only). Schema/provenance tests.
- **Skipped:** `Vf07_Blessing_WeakerRefreshKeepsStronger` (10→3: reference retains 10, Athena overwrites in `CharacterStatusEffectState.Start`, `src/MapServer/World/CharacterStatusEffects.cs:68`).
- **Not independently verified:** all other VF-01 fields (stats, BATK, MATK, DEF/MDEF, HIT/FLEE/CRI, motion, speed, HP/SP); Blessing/IncAGI timers, deadlines, identity, expiry; Increase AGI at all; mob attack roll (VF-14 only holds loader values: Poring `Attack: 1` in `db/re/mob_db.yml`, loaded `batk 7, watk 0, watk2 1`. Bounds 0..1 are plausibly consistent with the pinned integer calculation for Attack=1 — NOT classified as a discrepancy; only the executed attack roll and the BATK contribution can establish one); host support only macOS tested; VF-14 extension not rerun twice (do that first).
- Evidence note: current `MobBasicAttackCalculator` (src/MapServer/Gameplay/Rules/Renewal/) rolls `Random.Next(min,max+1)` (inclusive) and ignores BATK; `MovementSpeedCalculator`, `AttackDelayCalculator`, `PlayerDamageMotionCalculator` model Novice/fist/dagger only; `PlayerCombatSnapshot` carries BaseLevel/VIT/AGI only.

## B. Proposed architecture (smallest change that fits the code)
Existing authority: `CharacterGameplayStateSession` (single `_mutationLock`, persisted allocated stats + `Version`) is the durable owner; `CharacterStatusEffectState` (per `MapClientSession`, transient, own lock, TimeProvider) owns temporary statuses; `MapClientSession` calls `Recalculate()` at ~8 sites (movement 1656, ASPD 2964, 3274, 3340, 3731, expiry 926) — these independent reads are the incoherence the research flagged. Orleans/World holds no stat state and stays that way.

Pipeline (pure, in `src/MapServer/Gameplay/Rules/Renewal/StatusCalculator.cs`, new, justified by 8 duplicate consumers; replaces ad-hoc `Recalculate` math; stages mirror `status_calc_pc_` → `status_calc_bl_main`):
1. `StatusCalculationInput` (record): allocated stats, base/job level, job id (generated progression data), equipment contributions (empty until 15.6), active statuses (`ActiveStatus[]` snapshot), config constants.
2. Base rebuild (`status_calc_pc_`: base stats + job bonuses + base HP/SP/BATK etc.) → `BaseStatus`.
3. Status overlay (`status_calc_bl_main` with flags: str/int/dex/agi, batk, hit/flee/cri/def, maxhp/sp, aspd, speed, dmotion) → `EffectiveStatus`. Overlay never rewrites base; stage order follows source, not a generic additive bag.
4. Resource transition: pure function `(previous current/max, new max) -> clamp` — no heal on buff, no heal on expiry; persisted max stays a projection; temporary values never persisted.
Atomic snapshot strategy (revision pairs alone are not enough — they only detect, they don't prevent mixed reads). Rules:
- **Lock order is fixed and one-directional:** `CharacterGameplayStateSession._mutationLock` is the only lock held across `await`/persistence. `CharacterStatusEffectState._gate` stays a leaf lock: short, synchronous, never held while acquiring any other lock and never taken before `_mutationLock`. No code ever waits for `_mutationLock` while holding `_gate` (verified by test + comment).
- **Durable-input mutations** (stat-up, level-up, equipment): inside `_mutationLock`, read `State`, take the status snapshot (`_gate` leaf, copy of `ActiveStatus[]` + `Revision`), compute the candidate effective snapshot, persist, then publish `CharacterStatusSnapshot` (immutable record containing the exact inputs: `GameplayState` object, active-status copy, computed Base/Effective) via one volatile reference swap. Failed persistence publishes nothing.
- **Transient-input mutations** (status start/refresh/expire): take the status transition under `_gate`, then compute from the latest *published* `GameplayState` object and publish a new immutable snapshot with a compare-and-swap loop on the published reference (retry if a durable mutation published a newer state in between; at most a few iterations, no locks). The snapshot is self-contained, so a reader can never see gameplay state from one point and statuses from another.
- **Readers** (combat, movement, ASPD, projections) read the single published reference once and use it; revisions are carried only for diagnostics/staleness checks (e.g. committed attack keeps its snapshot on retry), never as the consistency mechanism. Tests: stat-up vs buff-expiry stress, refresh vs expire, a reader asserting snapshot self-consistency (recompute from its own inputs equals its outputs), lock-order test (no `_gate` held while awaiting).
- Gate: if any caller genuinely needs both locks held simultaneously, stop and ask.

Status transitions return an outcome (`Started | Replaced | Retained | Rejected`, plus old/new deadline) from `CharacterStatusEffectState.Start` so `MapClientSession` knows whether to resend icons/stat packets and whether the old expiry timer identity stays valid. Expiry removal is keyed on `(statusId, deadline/instanceId)` to stop stale timers removing a newer instance.

## C. Stages
Common per-stage method (reference-driven TDD): (1) extend `reference_runner.cpp` (real calls only; add to hook-build wrapper only for access, never change algorithms) → (2) `pwsh tools/rathena-reference/run.ps1` twice, byte-identical → (3) add conformance tests (red first; no placeholder skips — uncovered mechanics are tracked in the matrix) → (4) minimal production change → (5) focused tests, then `dotnet test tests/MapServer.Tests` (+ other projects if touched), `git submodule status`, `git diff --check` → (6) commit, push, update PR checklist, check `gh pr checks 47`. Update matrix rows (Implemented/Partial/Missing/Unverified/Deferred + fixture IDs + C# owner + tests + unsupported conditions) in the same commit as the mechanic.

### T0 — baseline hardening (before 15.2)
Rerun runner twice for VF-14 determinism; add a small loader helper to read named VF-01 fields (no placeholder/skipped tests: uncovered fields are tracked as Unverified/Missing in `renewal-status-matrix.md` and get executable conformance tests in the stage that implements the mechanic); CI-flake investigation above; save execution plan doc. Linux/Windows runner: document unverified; verify Linux via the GitHub `ubuntu` job only if cheap (optional CI step is out of scope unless approved).

### 15.2a Blessing / Increase AGI
- Source: `status.cpp` `status_change_start` overlap block (~10748–10800), val settings (11567 Blessing; IncAGI val2=2+lv), install/end (`status_change_end`, timers), `sc_start` → `status_change_start`. Verify via runner, not assumption: the reference's actual retention check for 10→3 lives outside the visible overlap switch (probably `skill`/`sc_start` caller or `status_change_start` flag path) — find it with C++ LSP and capture real behavior.
- Runner: Blessing/IncAGI sequences (same-level, lower, higher; refresh before expiry); record `val1..val4`, return value, `sce->timer` id changed?, deadline via `get_timer(...)->tick - gettick()` after a controlled tick (use `gettick` offsets / timer manager; if wall-time nondeterministic, assert relative deltas only), expiry via `status_change_end` + calc; IncAGI AGI/speed/aspd effects; Blessing cleanup of Curse/Stone on PCs is out of scope (note).
- C#: `CharacterStatusEffectState.Start` returns transition outcome; retain-stronger/equal/replace semantics exactly as fixture; deadline identity (do **not** assume equal refresh keeps deadline); expiry guard against stale timers; adapt `MapClientSession` callers (icon/stat packet only on replace/start — respect iRO packet notes, do not change wire shapes) and `StatusIds` consumers; enable skipped test; add IncAGI tests with `ControllableTimeProvider`; concurrency test (refresh vs `ExpireDue`).
- Files: `CharacterStatusEffects.cs`, `MapClientSession.cs` (status call sites), `CharacterStatusEffectStateTests.cs`, `RathenaReferenceFixtureTests.cs`, runner + fixture.
- Acceptance: all VF-07/08 sequences active (zero skips for supported scope), deadline/identity assertions, existing movement/status tests green. Risks: client icon resync on retained refresh (unknown iRO behavior → gate if packet change needed). Depends: T0.

### 15.2b Mob attack
- Source: `battle_calc_base_damage` (static, battle.cpp:2515), `status_base_atk_min/max` (status.cpp ~2522–2554), mob load (`mob.cpp` ~5098–5118), `battle_calc_defense_reduction`, `battle_calc_attack` tail, RNG `rnd_value`/`rnd()` (common/random).
- Runner: reach real calc via public `battle_calc_attack(BF_WEAPON, mob_md, sd, 0, 0, 0)` using a spawned/constructed `mob_data` for Poring (1002) vs the VF-01 Novice on prontera; seed RNG deterministically through rAthena's own seeding API, capture damage over a set of seeds + min/max observed; if static internals are unavoidable, add a wrapper in the disposable build (documented, not touching algorithm). Cases: Poring vs Novice, equal-bounds case (an `Attack`-only mob), optional GPoring (distinct ID, existing test monster) — never mixed.
- C#: compare observed min/max/exclusive bound and BATK effect against `MobBasicAttackCalculator`; then fix BATK contribution and rnd bound (`rnd_value(min,max)` inclusive vs `rnd()%(max-min)` — decided by the capture). Preserve idempotent logical attacks, retry, `MonsterAttackCadenceExecutor` ordering; existing tests that encode old behavior are corrected only with captured evidence (documented in commit).
- Acceptance: controlled scenarios match reference damage ranges; persistence/retry tests unchanged. Depends: T0. Out of scope: hit/flee, crit, elements, cards (Epic 17).

### 15.3 Snapshots + job bonuses
- Source: `status_calc_pc_` base assembly, job-bonus (`pc_calc_...`/`job_db` stats bonus at base/job level), `status_calc_bl_main`/flags; VF-02/03/09/10/19.
- Runner: Novice at various job levels, a 1st job and (if generated data supports) a second job with job-level bonus; allocated vs total; stat-up (`pc_statusup`) transitions; VF-19 stale-projection race is a C# concurrency test, not a reference fixture.
- C#: introduce `StatusCalculationInput`, `CharacterStatusSnapshot`, `StatusCalculator` (stage 2/3 above, stats only); `CharacterStatusEffectState.Recalculate` delegates (keep API until all callers move); `MapClientSession` call sites consume snapshots; `PlayerCombatSnapshot` takes effective stats from it; progression/stat-up compute candidate **inside** `_mutationLock` (fix compute-before-gate). Generated job bonus arrays reused (no importer change unless missing → gate: "maintaining-generated-world-content").
- Acceptance: six effective stats equal reference for covered jobs/levels/statuses, base vs effective distinct, no job-bonus omission/double count, race tests (stat-up vs buff expiry) green. Gate: if lock ordering between session mutation lock and status lock can't be done without holding both → ask.

### 15.4 Derived stats + HP/SP
- Source: `status_calc_misc`, `status_calc_batk/matk/hit/flee/def2/mdef2/cri/…` (status.cpp ~7500–7600 region), `status_calc_maxhp/maxsp_pc` (+`status_get_hpbonus` seen in the crash trace), regen amount (`status_calc_regen`), config rates.
- Runner: full VF-01 comparison table across levels/stats; Blessing/IncAGI on/off; VIT/INT/JOB/BASE level changes; max-HP-reduction + current clamp via real `status_calc_pc` + `status_set…`/`pc_` helper that clamps (verify which).
- C#: extend `StatusCalculator` with typed intermediates that mirror the exact numeric type of each source stage (int/short/uint narrowing, `float` where the source uses float, `double` where the source uses double — e.g. HP/SP stages; no blanket rule either way, each type is read from the cited source line and boundary-tested); resource transition function; `CharacterProgressionService`/`CharacterStatService`/`CharacterHealService` use it; no persistence schema change (persisted max remains the base-projection; decision gate if current clamp can't be durable without migration).
- Acceptance: all VF-01/02/04/08/11/13/16 fields in scope compared and passing; buff on/off doesn't heal; persistence failure → no success projection and state unchanged. Out: periodic regen ticking (15.8+), equipment, skills.

### 15.5 ASPD / speed / dmotion
- Source: `status_calc_aspd`/`status_calc_fix_aspd`, `amotion` and `aspd_max` config conversion (`battle.cpp` config adjust), `status_calc_speed`, `dmotion` (`status_calc_dmotion`/`battle_config.pc_damage_delay`), job_aspd table.
- Runner: job/weapon matrix limited to supported (Novice fist, dagger if equipment is real in 15.6 — order: ASPD for fist only first), IncAGI effect, cap conversion (190 in config → amotion units), damage motion, walking speed.
- C#: replace raw-190/hardcoded-460 uses; typed units (`AttackMotionMs`, `AttackDelayMs`, `DamageMotionMs`, `WalkDelayMs`); scheduler and projections read one snapshot; committed attack retains its snapshot on retry. Dependency: job ASPD data (generate only if consumer needs it).
- Acceptance: VF-08/12/20 match; no unsupported weapon silently becomes Novice (throws/unsupported marker kept).

### 15.6 Minimal equipment inputs
- VF-05/06/10/15: armor hard DEF, weapon ATK, dagger size modifier, equipment stat bonuses where item data already exists in generated items; reference runner needs real item objects (`pc_equipitem` path or `sd->inventory` setup + `status_calc_pc`); if equipment setup in the runner needs fabricated stubs → stop and ask. Files: `EquippedWeaponResolver`, `CharacterEquipmentMutation*`, `ItemDefinition`/item compiler only if a needed field is missing (generated-content skill rules apply). Unsupported item effects raise explicit "unsupported" rather than zero.

### 15.7 iRO sync
- Evidence first: read `ai/iro-2026-wire.md`; only already-verified packets (0x00BC ack, 0x00B0, 0x0141, status notifications) change; projection built from the committed snapshot, derived packets before stat-up ack; no packet code in calculator. Where captures are missing → list exact scenarios and **ask** before coding. VF-19 stale-projection regression + persistence-failure-no-ack tests.

### 15.R Regional GameRules architecture (added before closure; completed on this branch)
- **Why:** pAthena must let iRO and kRO gameplay evolve independently while staying on the verified iRO client and the pinned rAthena Renewal behavior today. Ruleset, gameplay region and client protocol are three separate concepts; region is per MapServer deployment, chosen once at startup, and one Orleans world must not mix regions.
- **Migration plan (executed as focused commits):** (1) `gameplay_region` config + options (strict parse, default iRO); (2) extract `ICombatRules`, `IStatRules`, `ITimingRules`, `IStatusEffectRules`, `IExperienceRules` for the mechanics that exist, with independent `Iro*`/`Kro*` classes carrying their own copy of each algorithm; (3) migrate consumers (`MapClientSession`, `MapServerWorld`, `MonsterCombatCoordinator`, `MonsterAttackCadenceExecutor`, `CharacterStatusEffectState`, `CharacterProgressionService`, stat-up path) to the abstractions and delete the static calculators; registration through the host's existing service collection (`AddGameplayRules`); (4) guard + parity + per-region fixture conformance tests; (5) world-level profile guard (`IWorldGameplayProfileGrain`, first MapServer fixes the profile) and documentation.
- **Shared on purpose:** immutable models/inputs/outputs, generated data, status storage/locking/snapshots, sessions, persistence, Orleans authority, packet serialization, experience rates (configuration policy), `JobSupport` verification metadata.
- **Evidence:** every golden-fixture conformance test runs once per region (`IroRathenaReferenceConformanceTests`, `KroRathenaReferenceConformanceTests`, and the other `Iro*/Kro*` subclasses); `RegionalRulesParityTests` compares both regions over seeded grids; `RegionalRulesArchitectureTests` enforces independence (no cross-region reference, no region branches, sealed, no shared static calculators, consumers name no concrete rule class).
- **Not claimed:** official kRO or iRO gameplay compatibility (both follow rAthena), kRO packet/client support (future work with its own evidence plan).
- **Decision recorded:** the world-level profile check adds `IWorldGameplayProfileGrain` (in-memory, deactivation delayed) to the World contracts - the only Orleans contract change in this stage, required by "one world must not silently mix regional gameplay".

### 15.8 Closure
Full suites (all test projects, `WorldDataImporter` + `git diff --check` if generated), one normal full run of the affected suites (no repeated stress loops - `CLAUDE.md` bounded debugging), reconnect + cross-replica tests, fixture regeneration twice identical, submodule unchanged, matrix final pass with Implemented/Partial/Unsupported/Deferred(16/17/18)/Unverified lists, PR checklist final.

## D. Commit sequence (single branch)
1 docs: execution plan  2 test: runner determinism + named-field fixture helper (+ CI flake investigation commit(s) if needed)  3 test: Blessing/IncAGI fixtures  4 fix: status transition outcomes + stale-timer guard (+ enable skipped test)  5 test: mob attack capture  6 fix: mob BATK/roll bound  7 test: job-bonus fixtures  8 feat: StatusCalculator stats stage + snapshot/revision  9 refactor: consumers on snapshot (separate from 8)  10 test: derived stat fixtures  11 feat: derived stats  12 feat: HP/SP + resource transition  13 test+feat: ASPD/speed/dmotion (fixture and implementation separate commits)  14 test+feat: minimal equipment  15 feat: iRO projection (if evidence)  16 docs: matrix/closure. Fixture commits always precede the behavior commit.

## E. Fixture coverage map (VF → mechanic → method → consumer → test → status)
VF-01 base/derived (15.4) · VF-02/03 job+levels (15.3) · VF-04/11/13/16 derived+HP/SP (15.4) · VF-05/06/10/15 equipment (15.6) · VF-07/08 Blessing/IncAGI (15.2a, 15.5) · VF-09/19 snapshot/races (15.3; 19 is C#-only) · VF-12/20 ASPD/speed/dmotion (15.5) · VF-14 mob attack (15.2b) · VF-17/18 regen rate/ticking (deferred 15.8+). Each ends "Reference executed + C# active" only when the matrix row says so.

## F. Risk register
1. Runner cannot reach a static function (mob damage) → public `battle_calc_attack`/wrapper in disposable build; else ask. 2. RNG determinism → seeded rAthena RNG, multiple seeds, assert sets. 3. Mixed state / lock-order deadlock → immutable self-contained published snapshot, `_gate` leaf lock never held across `_mutationLock`, CAS publish for transient changes; ask if both locks must be held. 4. Max-resource persistence/heal-on-buff → pure transition function, no schema change, ask if needed. 5. Orleans retries/idempotency → snapshot captured at attack commit; retry reuses it; existing combat idempotency tests unchanged. 6. iRO divergence/missing captures → gate. 7. C++ arithmetic fidelity (per-stage int/float/double, narrowing) → types taken from source per stage + boundary fixtures. 8. Flaky CI test → T0. 9. Docker runtime (~4 min builds) → fixtures committed, regen deliberate. 10. Scope creep to Epic 16/17/18 → explicit Unsupported markers.

## G. Decisions
Mine: type names/file placement, runner internals, how timers are sampled, test layout, commit grouping. Need approval: schema/persistence change, Orleans contract change, unresolved lock ordering, reference-vs-iRO conflict, missing captures, equipment-script/skill/Epic-17 scope, runner stubs, final merge.

## H. Definition of Done
Every in-scope row in the matrix is Implemented (reference fixture + active test) or explicitly Partial/Unsupported/Deferred; all fixtures `ReferenceExecuted` and byte-reproducible; no skipped or placeholder conformance tests (the 15.1 Blessing skip is enabled in 15.2a); full test suites + CI green (flake investigated); packets only per iRO evidence; submodule untouched; PR checklist complete; user approves merge. Excluded: periodic regen ticking, complete equipment/cards/refine, skills, full combat (Epic 17), fourth-job traits.

## I. Next immediate action after approval
T0: rerun `pwsh tools/rathena-reference/run.ps1` twice to confirm VF-14 determinism, then extend the runner with Blessing/IncAGI deadline+timer capture (stage 15.2a first fixture commit).

## Closure status (living section; mirrors PR #47 and `renewal-status-matrix.md`)

Evidence types are kept apart: **R** = reproduced against real pinned-rAthena execution (fixtures VF-xx, `ReferenceExecuted`);
**I** = verified stock-iRO capture; **S** = source-read only.

| Mechanic | State | Evidence | Supported scope / remaining |
|---|---|---|---|
| Blessing / Increase AGI start, refresh, retention, end | Implemented | R (VF-07/08) | Timers sampled at 50 ms granularity; client resend on retained start intentionally none (I missing) |
| Mob basic attack (BATK, exclusive roll, soft/hard DEF) | Implemented for ordinary non-skill attacks | R (VF-14/15) | Accuracy/FLEE, crit, elements, cards unmodeled (Epic 17) |
| Six stats incl. job-level bonuses | Partial | R (VF-02/16) | `JobSupport`: 35 verified classes; unverified: transcendent/fourth/Hyper Novice/Kagerou (documented) |
| BATK/HIT/FLEE/perfect dodge/CRI/DEF2/MDEF2/MATK/hard DEF | Partial | R (VF-01/02/05/07/08/16) | Armor DEF only; no refine/cards/scripts/passives; weapon MATK rejected |
| Max HP/SP + VIT/INT resource transition | Partial | R (VF-04/11/16) | x1.25 upper/fourth and Taekwon-ranker multipliers unmodeled; no equipment/skill HP bonuses |
| ASPD / amotion / adelay | Partial | R (VF-17) | Non-ranged weapons for verified jobs; DEX-branch weapons throw; third/summoner/extended max_aspd 193 unmodeled |
| Walk speed, damage motion | Implemented for modeled statuses | R (VF-02/07/08) | Mount/Run/slow statuses unmodeled |
| Attack animation speed on the wire | Implemented | R (VF-02/17 values) + contract test | Orleans `WorldMonsterDamageCommand.SourceAttackMotion` (Id 6) |
| Equipment: Knife / Cotton Shirt loadouts, size fix table | Partial | R (VF-05/06/15) + importer tests | Only generated items; Dagger size modifier 100/75/50 (earlier "no-op" claim corrected) |
| Client stat-up burst from one snapshot | Partial | I (existing) | Max HP/SP resend and derived cascade need captures (see `ai/iro-2026-wire.md`) |
| Natural regen amounts/ticking, traits, skills, cards, refine, full combat | Deferred | - | Epics 16/17/18 or later approval |

Open follow-ups: iRO captures listed in `ai/iro-2026-wire.md`; generator support for map.hpp job flags (upper/third/fourth) to
retire `JobSupport` divergences; Super Novice `die_counter`; weapon MATK; Kagerou/Oboro SP table discrepancy at level 1.

## Movement-retarget race (bounded investigation, fixed)

`MapClientSessionMovementRetargetTests.RetargetExactlyAtStepBoundary_IsAppliedAtThatBoundary` timed out once in 25 local runs (and
earlier CI runs showed related timing failures). Read-only analysis of `HandleIroMovementAsync` found a genuine time-of-check/time-of-use
race rather than test noise: the "is a step in flight" check (`movementState.IsMoving`) and `RequestRetarget` ran **without**
`_movementGate`, while `ProcessDueMovementAsync` advances under that gate. A retarget arriving exactly as the final step completed could see
`IsMoving == true`, lose the race to the movement loop (walk finished, nothing pending to consume), and then park its destination on a
finished walk where nothing ever consumes it - the click was silently dropped and the client never got a movement response (the observed
timeout). Fix: check and record under `_movementGate`, so the retarget either defers to a walk that will cross another boundary or sees the
finished walk and starts a fresh one. The existing boundary test and the other 91 movement tests pass; there is no deterministic regression test
for the interleaving (it needs a hook between the check and the record), so this stays classified as *fixed with reasoning, not reproduced
deterministically*. Earlier CI failures in `KillThroughRealAttack…` and `RealRathenaOnTouch…` were timing tests under CPU starvation and were
mitigated by parsing the reference fixture once per run; their root cause was not separately proven.

## Gameplay-profile guard lifecycle (hardened)

The World owns an immutable, deployment-configured profile: `ATHENA_WORLD_GAMEPLAY_REGION` (`Iro` default, `Kro`) and
`ATHENA_WORLD_GAMEPLAY_RULESET` (`Renewal`); invalid values fail World startup. There is no first-MapServer-wins. Every silo must be
given the same values.
- `IWorldGameplayProfileGrain` is stateless: any activation (first, re-activated, failed over to another silo) answers from its hosting silo's
  configuration, so the profile survives silo restart and failover without persistence. A mismatched request is rejected, never adopted.
- A silo startup task registers its own profile with the cluster; a silo configured differently from the cluster fails to start.
  Limit: agreement is checked when a silo starts/joins (against the current activation host), not continuously.
- MapServer checks the profile at startup and again on every recovered Orleans connection (`WorldConnectionWatchdog.ConnectionRecovered` ->
  `WorldGameplayProfileMonitor`); mismatch or an unverifiable World stops MapServer with exit code 78 (fail closed).
- Tests: `WorldGameplayProfileConfigurationTests`, `WorldGameplayProfileGrainTests` (incl. reactivation), `WorldGameplayProfileClusterTests`
  (startup mismatch, same-profile join, hosting-silo failover), `WorldGameplayProfileMonitorTests`, `WorldConnectionWatchdogRecoveryTests`.
- Not done: AppHost does not set the variables (defaults iRO/Renewal apply); kRO gameplay/protocol compatibility remains unverified.
