# Epic 15 implementation and independent verification plan

This is a **reviewable research baseline**, not an exhaustive audit or authorization to implement gameplay changes. Reference: rAthena **`e985006171d2eb320ee512a653f4c83aea3d81b6`**; Athena baseline **`a3d006aa07a376d42c08187ea45cebc3c65384f4`**. All current changes are confined to these three research documents. The [mechanic matrix](renewal-status-matrix.md) records source/formula/coverage evidence; the [dependency matrix](renewal-status-dependencies.md) records order, transitions, iRO projection and distributed constraints.

The immediate goal after research approval is to establish an independent oracle and fix demonstrated errors in small PRs. Complete equipment scripting, all skills, all statuses, fourth-job gameplay and the entire combat pipeline are separate work. A supported subset must be explicit and behaviorally exact within its inputs; an unsupported mechanic cannot silently become zero, a Novice default or an approximation.

## Priority findings

| Priority | Finding established by static source inspection | Implication |
|---|---|---|
| P0 | Ordinary mob damage adds enabled `STR+BaseLevel` BATK; current calculator omits it. Unequal noncritical bounds use `rnd()%(hi-lo)+lo`, excluding hi; Athena includes hi. [Mob load][R-mobload], [base ATK][R-baseatk], [attack][R-baseattack] | Existing GPoring “authoritatively a miss” test certifies incorrect input math. Correct through independently verified fixtures, not another hand-rewritten formula. |
| P0 | Default status overlap keeps a stronger buff and its existing deadline when a weaker one is applied. Athena unconditionally replaces it and tests that behavior. [Overlap][R-overlap], [DB][R-statusdb] | Blessing and Increase AGI transitions need distinct retained/replaced/failed outcomes and timer identity. |
| P0 | Job bonuses are constructed before effective stats in the reference. Athena uses job VIT/INT for progression maxima but omits job bonuses from effective combat/client stats. [Assembly][R-jobstats] | Same character currently has inconsistent stat inputs across consumers. Centralize snapshots before widening scope. |
| P1 | HP/SP helpers use staged doubles, class/rank/equip/status bonuses and caps; current maxima only update on level change using a limited integer expression. [HP][R-maxhp], [SP][R-maxsp], [caps][R-maxdelta] | VIT/INT allocation, Blessing and equipment can leave stale maxima; separate recalculation from level-up refill. |
| P1 | Raw ASPD190 is converted to delay200 before `pc_maxaspd`; Athena uses190 directly and clamps motion95 instead of100. Job/weapon support is also restricted. [Config][R-confadjust], [cap][R-aspdcap] | Fix units and job ASPD inputs; do not claim all jobs work because generated progression contains them. |
| P1 | Root size DB sets dagger Medium75/Large50; RE overlay does not remove it. [Root][R-sizedb], [loader][R-sizeload], [application][R-sizebattle] | Existing dagger damage scope and tests omit a real modifier; GPoring is Medium. |
| P1 | Hard DEF is missing; soft DEF is recomputed from effective inputs instead of staged base+delta history. [DEF][R-def], [delta][R-defdelta] | Armor and temporary modifiers cannot be modeled by hardDEF0 and a single fresh formula. |
| P1 | Progression candidate is calculated before the mutation gate; combat/projection paths read gameplay state across awaits; effect before/expire/after is not atomic | Versioned immutable transition snapshots are needed to avoid mixed or overwritten state. Preserve existing durable versioning and combat retry ledger. |
| P1 | iRO stat-up ends with ack after derived updates; captured buff cascades precede their icons; current implementation sends only a subset and activation order differs | Build packet fixtures from sanitized evidence. Do not copy rAthena packet order or mislabeled capture parameter names as iRO truth. |
| P2 | Derived HIT/FLEE/MATK/MDEF/CRI/PD/regen and trait architecture are missing; weapon/refine/passive/status inputs incomplete | Stage fields and mechanics with explicit supported scope; complete later systems only after separate audits. |

These are source comparisons, not measured end-to-end parity results. No reference executable or new official-client capture was run during this task. Deduced GPoring arithmetic in the matrix is not a golden fixture.

## Independent verification strategy

### Feasibility assessment

A small C++ **driver** is feasible in principle; a tiny standalone library containing only copied formulas is not an adequate oracle. The pinned build creates a full map-server executable from map sources and skill factories, linking common, rapidyaml, MySQL and optionally PCRE. `status_calc_pc_sub` executes scripts and reads global databases, equipment, skill trees, map/session context and timers; several requested helpers are `static`. `status_calc_bl_` also produces client side effects and consults delayed damage. Merely compiling `status.cpp` with every dependency stubbed to zero would reproduce an invented world. [Build][R-build], [PC pass][R-pcbase], [wrapper][R-wrapper].

No build experiment or harness was performed in this documentation task. Toolchain/library availability, startup/database requirements, cross-platform floating-point reproducibility and the minimum working initialization remain **unverified feasibility work** for the first implementation PR.

Preferred approach, after approval:

1. Use a disposable checkout at the exact pin, outside the read-only reference submodule. Record a clean tree and any adapter/instrumentation patch hash. Do not make Athena production depend on building/running rAthena.
2. Link the actual calculation translation units with their real dependencies, preferably as a diagnostic scenario runner using normal map-server initialization. For private static helpers, instrument their actual calling path or build a test adapter in the same translation unit; do not retype their formulas.
3. Load the normal root databases and mode-specific imports, adjusted battle configuration, jobs/ASPD/basepoints, item/refine/grade/size, skill and status data. Construct/register real player/mob/map context, initialize equipment indices, skill tree, timers and base/battle state through genuine source paths.
4. Capture packet-send calls into a diagnostic sink if needed; suppressing network transmission is acceptable only if it cannot affect gameplay branches. Any gameplay dependency that is uninitialized makes that scenario unsupported, not reference-verified.
5. Exercise both full rebuild and real start/refresh/end transitions. Static helper calls alone cannot prove flag propagation or delta-history behavior. Use actual mob DB loading before attacking, not a manually incomplete `status_data`.
6. For randomness, record seed/state or inject a test-only RNG boundary at the real RNG call without changing the modulo/endpoint expression. Capture raw draw and bound/rolled value. Separately test equal bounds, unequal width1 and upper boundary; report any seam patch. Do not replace source modulo with an inclusive C# draw and call it equivalent.
7. Export fixed JSON fixtures for C# tests and a regeneration manifest. Expected numeric results must come from the executable, not the C# implementation or spreadsheet estimates.

If a narrow driver cannot initialize real state economically, use a full pinned local map server with scripted/admin-controlled scenarios and instrumented snapshots or debugger probes. A reproducible full-server trace is a valid independent oracle; a second C# implementation of the same formula is not. If neither runs, keep fixtures marked “source-derived/manual” and leave the affected parity claim **Unverified**; submit the precise linker/startup/runtime blocker and reproducible commands as an oracle feasibility PR before proceeding broadly.

### Fixture provenance and diagnostics

Each fixture must include:

- Exact reference commit, adapter patch, compiler/version/target/flags (especially Renewal and FP), source/database/config hashes and loaded imports. No credentials or unsanitized packet payloads.
- Complete supported input: allocated stats/traits, job/class/sex/base+job levels, skills, item instances/slots/refine/cards/options, active status values/timers, map/context, relevant configuration and current resources. Explicitly distinguish absent supported input from unsupported input.
- Transition kind and logical timestamp, previous base/battle snapshot, requested/effective SCB flags, whether BASE or FORCE was used, and outcome (failed, retained, replaced, expired, committed).
- Intermediate stage outputs: equipment versus other parameters, job totals, primary base/effective, per-hand ATK/MATK and refine components, pre/post rates, HP/SP double stages and casts, base/delta DEF2/MDEF2, ASPD stages and config-converted cap, regen channels, final/display fields.
- For attacks: mob loaded status including BATK and bounds, raw RNG result, selected component, defense before/after attack-specific adjustments, final damage/outcome and immutable logical attack identity.

When final values differ, compare the earliest differing intermediate, then verify inputs, loaded data/imports, types/casts, order and configuration. Do not adjust a final constant until outputs happen to match. Fixture regeneration must be deliberate and reviewable; C# tests never regenerate their own expected values.

### Scenario matrix

All scenarios below need executable reference outputs; current unit tests are regression evidence only.

| ID | Scenario and distinguishing cases | Outputs / acceptance evidence |
|---|---|---|
| VF-01 | Fresh Novice, exact base/job level1, six allocated values, naked/unarmed, no skills/effects; separately the known starter state | Full base/effective/display snapshot with declared config; no assumption that all “fresh Novices” share the same allocation/equipment |
| VF-02 | Change each STR/AGI/VIT/INT/DEX/LUK independently at fixed levels | All direct and indirect outputs, points/cost and resource clamps; `/3`,`/5`,`/10` boundaries |
| VF-03 | Job levels immediately before/at six-stat bonus increments, without changing allocated values | Cumulative job data reaches combat and primary plus; no double-count in HP/SP |
| VF-04 | Base levels around table/level-factor boundaries; job levels independently; upper, ranker, Super Novice and primary-fourth cases as separately enabled scope | Table indexing, passive/class conditions, caps100/151, no generic job fallback |
| VF-05 | Unarmed then Knife; equip/unequip; different supported weapon family, shield/left hand only when implemented | Per-hand components, job ASPD, range, size and coherent equipment snapshot; unsupported states explicitly rejected/reported |
| VF-06 | Dagger against Small/Medium/Large; pinned GPoring |100/75/50 size stages, mob BATK, actual loaded mob definition |
| VF-07 | Blessing activation and expiration; level10→3,10→10,3→10 refresh; refresh immediately before old expiry | Retained stronger level/deadline, correct replacement timers, STR/INT/DEX and extra HIT, MaxSP/regen/ASPD, no stale timer removal |
| VF-08 | Increase AGI same activation/expiry/refresh variants | AGI, FLEE, DEF2, speed, ASPD and dmotion; mid-walk behavior; source DSPD restoration question |
| VF-09 | Both effects, both activation orders, same-time and separate expiration, refresh of one | Correct combined outputs, no transient mixed snapshot; observe any reference path dependence |
| VF-10 | Stat/equipment/job mutation while effects active; resource current value above newly reduced maximum | Total plus reflects all sources; current HP/SP clamps only as required; level-up healing remains distinct |
| VF-11 | Soft DEF odd `level+VIT`, AGI just below/at5; MDEF2 combined DEX+VIT thresholds; negative expiry deltas | Distinguish full-expression truncation, divided inputs and staged delta; include nondefault base rates |
| VF-12 | ASPD float/cast boundary, high AGI/DEX,190/193 limits, nondefault max ASPD/walk/dmotion rates | Float versus double evidence, converted cap units and correct amotion/adelay; snapshot/animation/cadence agreement |
| VF-13 | HP/SP double rounding near integer, equip-only VIT/INT, simultaneous fixed/equipment/item/status %, legal max caps | Earliest-stage diagnostics; no algebraic folding or accidental extra truncation |
| VF-14 | Mob attack bounds equal, width1, wider band, raw draw near maximum; BATK mask enabled/disabled | Exclusive hi, no roll for equal/reversed bounds, stored uint16 semantics; actual RNG provenance |
| VF-15 | Player zero/nonzero/negative hard DEF; target-crowd threshold; armor mutation and qualifying defensive passive | Source defense inputs, -400 guard, attack-context adjustments; full combat conditions outside scope remain labeled |
| VF-16 | Stat caps/cost99→100 and105 boundaries; invalid stat/amount, insufficient points, persistence failure | Reference validation outcome and Athena no-success/no-state-write failure contract; iRO failure packet remains separate evidence |
| VF-17 | Regen INT119/120/121/122, maxHP199/200, maxSP99/100, overweight/sitting/walking/dead/hidden and statuses | Amounts, rates, eligibility, tick accumulators; no fabricated catch-up policy |
| VF-18 | Trait `/3`,`/5` and negative delta/cap edges when fourth-job support is enabled | All six traits and PATK/SMATK/RES/MRES/HPLUS/CRATE ordering; no assumed zero traits |
| VF-19 | Persistence race, buff refresh versus expiration, equip versus attack snapshot, out-of-order projection | One coherent revision and durable transition; no stale projection or lost gameplay mutation |
| VF-20 | Attack timeout then buff/equip/job change before retry | Byte/field-identical command damage/sequence/life/presence; no reroll, duplicate HP mutation, action event or cadence reset |

VF-19/20 are Athena architecture tests, supplemented by relevant reference transition observations; they are not claims that rAthena uses Orleans. Client-facing tests must use sanitized official iRO fixtures, independently of the C++ oracle.

## Independently reviewable implementation stages

Stage numbers are priority/order, not a demand for one large PR per stage. Split any stage whose actual diff stops being independently reviewable. Every stage must declare its exact supported state combinations and leave unavailable mechanics explicit.

### PR 15.1 — pinned reference runner feasibility and first golden fixtures (recommended first PR)

- **Objective/mechanics:** establish independent proof for fresh Novice, job bonus, mob loaded BATK/upper bound, Blessing stronger/equal refresh and ASPD cap; VF-01/03/07/12/14.
- **Source:** [actual build][R-build], [base pipeline][R-pcbase], [mob load][R-mobload], [attack roll][R-baseattack], [overlap][R-overlap], [config conversion][R-confadjust].
- **Affected components:** test tooling and a small fixture reader in MapServer tests; no gameplay behavior change. Reuse existing generated definitions as comparison inputs, not expected-value generators.
- **New abstractions:** scenario manifest and immutable reference-output schema, justified by reproducibility and intermediate comparison. No general rule-plugin system.
- **Tests:** regenerate twice under recorded environment; verify hashes/pin, controlled inputs and actual-source outputs; C# fixture loader must reject inconsistent provenance.
- **Risks/exclusions:** global initialization and static helper access; exclude synthetic zero stubs, production dependency on legacy build, full combat/status coverage and raw capture storage.
- **Acceptance:** at least one actual PC rebuild and one actual transition/mob path runs with real dependencies, or a precise documented blocker plus full-server alternative is reviewed. No manually estimated expected values labeled verified. Research approval precedes work.

### PR 15.2 — correct the two narrow, demonstrated behavior errors

Prefer two focused PRs after their fixtures exist:

- **Objective/mechanics:** (a) monster BATK plus exclusive upper-bound behavior in the declared ordinary noncritical scope; (b) stronger/equal/lower Blessing and Increase AGI refresh semantics, including old-expiry identity.
- **Source:** [mob parse/load][R-mobparse] [R-mobload], [base][R-baseatk], [roll][R-baseattack], [overlap/install/end][R-overlap] [R-install] [R-end].
- **Affected components:** `MobBasicAttackCalculator` and its tests; separately `CharacterStatusEffectState`/status scheduling and tests. Reuse existing RNG seam, TimeProvider and outcome types where suitable.
- **New abstractions:** only an explicit roll endpoint contract and/or status transition outcome if current types cannot distinguish retained versus replaced. No new distributed service.
- **Tests:** VF-07/08/14 and existing pending-attack/expiry regressions. Replace misleading GPoring and weaker-refresh assertions with source-produced evidence; do not force captured unknown-state damage to match.
- **Risks/exclusions:** changes to previously accepted damage/miss outcomes; refresh packet behavior may need separate capture review. Full accuracy/critical/card/element/defense systems are excluded and remain gaps.
- **Acceptance:** ordinary mob input math and refresh transitions match declared reference fixtures; retry identity and persistence failure behavior unchanged; all remaining combat limitations explicitly visible.

### PR 15.3 — coherent input/base/effective snapshots and six job bonuses

- **Objective/mechanics:** RS-STR/AGI/VIT/INT/DEX/LUK/RS-JOB for current supported jobs/effects, with exact base/job/effective separation and deterministic stage ordering.
- **Source:** [assembly][R-jobstats], [flags][R-edges], [helpers][R-str] [R-agi] [R-vit] [R-int] [R-dex] [R-luk].
- **Affected components:** `CharacterGameplayStateSession`, `CharacterStatusEffectState`, `CharacterProgressionService`, stat service, generated progression consumer and combat snapshot. Reuse existing immutable records, mutation gate, TimeProvider and generated bonus arrays.
- **New abstractions:** immutable `StatusCalculationInput`, base/effective output and transition revision (names provisional), because independent live reads currently mix state. Preserve existing durable version rather than reuse it for transient expiry.
- **Tests:** VF-02/03/07–10/19; progression race regression; same revision consumed by combat/client; persistence failure emits no success.
- **Risks/exclusions:** lock ordering and stale precomputed progression candidates; no character grain, ownership migration, whole equipment script engine or fourth-job allocations.
- **Acceptance:** one authoritative calculation path for six effective stats, no job bonus omission/double counting, no compute-before-gate overwrite, supported refresh/expiry atomicity. Existing allocated-stat storage remains allocated.

### PR 15.4 — base derived fields and exact max-resource integration

Split resource integration from the other fields if needed.

- **Objective/mechanics:** RS-BATK/MATK/HIT/FLEE/DEF2/MDEF2/PD/CRI, HP/SP and regen **amounts** for the explicitly supported no-unimplemented-modifier scope.
- **Source:** [misc][R-misc], [battle deltas][R-defdelta], [MATK][R-matkdelta], [critical][R-critdelta], [HP/SP][R-maxhp] [R-maxsp], [caps][R-maxdelta], [regen][R-regen].
- **Affected components:** central calculator, progression, stat-up/effect transitions, `PlayerCombatSnapshot`, combat input adapters. Reuse generated HP/SP tables, persistence contracts and explicit heal service.
- **New abstractions:** typed intermediate fields only where source stages require them; base/effective distinction retained. Resource transition result carries new maxima and any required current clamp.
- **Tests:** VF-01–04/07–11/13/16 plus configured caps and exact casts; all fixtures compare intermediate stages. No same-formula expected-value helper.
- **Risks/exclusions:** changing persisted maxima/current clamps, temporary-effect persistence policy, negative/narrow arithmetic. No automatic periodic regen, spell damage, accuracy or full equipment modifiers in this PR.
- **Acceptance:** VIT/INT/job/buff transitions cannot leave stale maxima; progression refill is explicit; no consumer privately reimplements these fields. Unsupported input combinations are documented/detectable.

### PR 15.5 — timing, movement and animation consumers

- **Objective/mechanics:** RS-ASPD/SPEED/DMOTION for generated job/weapon data in declared scope, correct configuration conversion, effect transitions and all live consumers.
- **Source:** [ASPD base][R-aspdbase], [SC contributions][R-aspd], [fixed motion][R-aspdfix], [timing pass][R-timingdelta], [cap conversion][R-confadjust], [speed][R-speed].
- **Affected components:** job ASPD data generation (minimal concrete prerequisite), attack-delay/movement/dmotion helpers, combat scheduler, MapClientSession action/public-state projections. Reuse existing movement/feed APIs and pending-attack command state.
- **New abstractions:** explicit units/fields for attack motion, repeat delay, damage motion and walking delay; no generalized scheduler framework.
- **Tests:** VF-08/09/12/20, source float boundaries and job caps, no newer-state retry recalculation, identical fast/feed animation inputs.
- **Risks/exclusions:** cadence, active walking and visible animation changes; source DSPD/passive-ASPD quirks must be resolved by runner or remain blocked for those cases. No riding/shield/dual-hand support unless its exact inputs and fixtures are included.
- **Acceptance:** remove raw190 delay misuse and hardcoded460 where the supported derived field is required; one numerical source feeds scheduler and projections with correct units; no unsupported weapon becomes a Novice fallback.

### PR 15.6 — minimal real equipment inputs and existing attack consumers

- **Objective/mechanics:** RS-WATK/DEF/RANGE/SIZE and the minimum item/refine/hand inputs necessary for the declared equipped-character scope. Start with actual armor DEF and dagger size table import; refine/grade/MATK in additional small PRs only when covered.
- **Source:** [equipment/scripts][R-equip] [R-scripts], [bonus dispatch][R-bonus], [DEF][R-def], [size load/apply][R-sizeload] [R-sizebattle], [attack defense][R-defdamage].
- **Affected components:** `ItemDefinition`, generated item compiler, equipment snapshot/resolver, central calculator, weapon/mob combat inputs. Reuse authoritative inventory identity and existing generated data pipeline.
- **New abstractions:** per-hand and per-instance contribution records, justified by refinement and slot deduplication; typed modifier stage if a concrete bonus needs it.
- **Tests:** VF-05/06/10/15; item multi-slot deduplication, persistence failure reconciliation, reference stage arithmetic. Include reference import precedence test.
- **Risks/exclusions:** displayed and actual damage change, incomplete existing equipment transaction on conflict removal. Exclude complete card scripts, all passive masteries, status resistances, elemental/cardfix combat and item-use systems assigned later.
- **Acceptance:** equipped states in scope have real armor/weapon/range data; damage consumes derived defense; no “zero because unmodeled” result claims support. Partial equipment failures cannot publish a fictitious coherent set.

### PR 15.7 — complete verified iRO projection for supported calculations

Can proceed alongside calculator work once output contracts stabilize; requires capture evidence before adding uncertain behavior.

- **Objective/mechanics:** changed six-stat totals, derived displays, resources, timing/range and public speed from one committed revision; complete documented stat-up/buff bursts where verified.
- **Source/evidence:** [iRO wire](../../ai/iro-2026-wire.md) and existing sanitized regression fixtures are authoritative; [reference display][R-display] [R-projection] [R-wrapper] identify candidate fields only.
- **Affected components:** `MapClientSession`, existing iRO packet builders and integration fixtures, public presence/action adapters. Reuse serializers whose shape is already proven.
- **New abstractions:** immutable display projection and ordered transition writer only if needed to prevent post-await rereads; no packet types inside gameplay calculator.
- **Tests:** captured byte/field/order fixtures for all six upgrades, activation and controlled refresh/expiry when available; VF-19 stale projection race; persistence failure no ack; paired DEF/MDEF display mapping.
- **Risks/exclusions:** Captain table parameter-name inconsistencies, unknown rejection/multi-step/expiry behavior. Exclude speculative rAthena-only packets and official-client modification.
- **Acceptance:** derived packets precede stat-up success ack; supported captured activation order preserved; unchanged values do not generate accidental stale projection; all unverified behavior remains explicitly labeled.

### PR 15.8+ — separately approve broader mechanics

- **Objective/mechanics:** regen rate/eligibility/ticking, additional status and passive branches, complete refinement/bonus systems, fourth-job traits/substats/AP as scoped follow-ups.
- **Source:** [regen amount/rate/timer][R-regen] [R-regenrate] [R-regentick], [traits][R-traits] [R-traitdelta] [R-traitbonus], [equipment/scripts][R-equip] [R-scripts], per-feature status/skill source.
- **Affected components:** only the concrete consumer for each feature plus its data/persistence contracts. Reuse calculation stages and fixture schema; no speculative service hierarchy.
- **Required tests:** VF-17/18 and feature-specific complete success/failure/transition reference scenarios; iRO evidence for newly exposed fields.
- **Risks/exclusions:** periodic durable HP/SP mutations, larger combat scope and distributed contract/schema changes. Full equipment/skill/combat systems are not implicitly included in Epic15.
- **Acceptance:** each feature has exact inputs, source paths, branch/cast/order fixtures, failure behavior and capability boundaries before enablement. Declare remaining unsupported states; no broad “complete Renewal” claim from a subset.

## Architecture decisions requiring review before implementation

Research approval is the next decision gate; no gameplay code has been changed here. Subsequent PRs need concrete designs for:

1. **Max-resource persistence:** keep persisted max HP/SP as a projection compatible with the existing contract, or change the schema. Determine how transient effects and a failed durable current-resource clamp are handled without persisting temporary attributes or healing on expiry. No schema migration is assumed.
2. **Atomic snapshot boundaries:** coordinate allocated stats, inventory, skills, transient effects and movement with existing session gates and runtime revision; specify lock ordering and calculate candidates inside the authoritative mutation. This is a local ownership clarification, not a distributed migration.
3. **Oracle placement/build:** development-only disposable checkout/full-server adapter and licensing/distribution implications if any reference code/artifacts are redistributed. No production dependency on rAthena.
4. **Supported scope enforcement:** document which job/weapon/equipment/status combinations are exact, and how callers surface unsupported combinations. Avoid a blanket runtime rejection that unexpectedly disables existing working flows; review the concrete behavior and migration before shipping.
5. **Shared assembly or World contract changes:** only if a second consumer needs the same pure code. Keep formulas single-source; do not move character authority or add grains/network fetches without justification.
6. **Source quirks and iRO differences:** retain reference behavior unless executable/reference or verified official evidence establishes otherwise. Any intentional divergence must identify evidence and be approved explicitly, never hidden as a cleanup.

## Remaining investigations and limits

This task deliberately stops at the established research baseline. These items are visible follow-up work, not silently completed claims:

- Build/run the independent reference runner; no numerical fixture in this PR is execution-verified.
- Resolve source path-history quirks with transitions: DSPD on final AGI buff expiry, passive ASPD bonuses gated by nonempty SC, Renewal WATK left/right asymmetry, trait helper/delta cap order.
- Validate every permitted numeric range and target compiler's narrowing/FP behavior. Undefined signed overflow or out-of-range float conversion has no portable “exact” C# equivalent to invent.
- Complete the branch-by-branch audits of all status helpers, resistance/casting validation, passive skills, `pc_bonus2/3/4/5`, item/card/random-option/combination scripts, refinement/grade data and attack-time modifiers before those features are enabled. The linked source ranges are not a claim that every combination was proved.
- Verify full job import/table coverage, special class factors and fallback formulas against generated outputs; generated job identity alone does not establish job gameplay support. Generate ASPD/traits only when their consumer is ready.
- Capture controlled iRO stat/equipment/buff/refresh/expiration/rejection scenarios and re-extract the Captain parameter IDs/names/order. Existing documented burst labels are inconsistent; official val1 differs from pin, with cause unresolved.
- Establish complete input state before interpreting captured GPoring37/0 damage as an oracle. Missing state and accuracy cannot be reverse-engineered into invented constants.
- Audit all context-driven recalc triggers (map/riding/casting/weight/rank/death/pet/script) and precise same-tick effect-removal ordering. The dependency matrix covers the inspected PC foundation and common triggers, not every caller.
- Validate resource persistence and projection failure/reconnect policies, equipment partial-persistence reconciliation, and shared-snapshot races before adopting the proposed architecture.

## Research validation and delivery

The research PR must contain only:

1. `docs/gameplay/renewal-status-matrix.md`
2. `docs/gameplay/renewal-status-dependencies.md`
3. `docs/gameplay/renewal-status-implementation-plan.md`

Validation for this baseline consists of pinned submodule/worktree verification, targeted verification of critical source formulas/branches, local path and pinned line-range checks, cross-document consistency, and `git diff --check`. No gameplay tests were added or changed, and no harness was built. Local checks passed: all 42 relative document/source links and 103 unique pinned source ranges resolved; the reference submodule remained clean at the required commit; release-version, release-image and release-workflow-rule script tests passed. The .NET server suites, package-vulnerability scan and live-registry test were not run locally for this documentation-only change. Existing repository CI remains applicable to the PR; its status must be reported separately from source-equivalence evidence. A passing existing test suite does not certify this research's unimplemented mechanics.

The recommended first implementation PR is **15.1: actual pinned-source runner feasibility and a small set of golden fixtures**. It makes the known monster/refresh corrections reviewable and gives the later central calculator an independent acceptance standard. This research PR is for review and must not be merged automatically by the agent.


## Pinned source links

All links below resolve to commit `e985006171d2eb320ee512a653f4c83aea3d81b6`. Ranges are inclusive; no upstream branch URL is used.

[R-mobload]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/mob.cpp#L5537-L5595 "src/map/mob.cpp:5537–5595 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-baseatk]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L2424-L2496 "src/map/status.cpp:2424–2496 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-baseattack]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/battle.cpp#L2518-L2636 "src/map/battle.cpp:2518–2636 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-overlap]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L10748-L10806 "src/map/status.cpp:10748–10806 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-statusdb]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/db/re/status.yml#L445-L481 "db/re/status.yml:445–481 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-jobstats]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4216-L4348 "src/map/status.cpp:4216–4348 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-maxhp]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3511-L3553 "src/map/status.cpp:3511–3553 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-maxsp]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3562-L3604 "src/map/status.cpp:3562–3604 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-maxdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6198-L6239 "src/map/status.cpp:6198–6239 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-confadjust]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/battle.cpp#L8962-L8980 "src/map/battle.cpp:8962–8980 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-aspdcap]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.cpp#L15251-L15259 "src/map/pc.cpp:15251–15259 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-sizedb]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/db/size_fix.yml#L30-L88 "db/size_fix.yml:30–88 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-sizeload]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L491-L581 "src/map/status.cpp:491–581 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-sizebattle]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/battle.cpp#L2427-L2440 "src/map/battle.cpp:2427–2440 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-def]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7748-L7848 "src/map/status.cpp:7748–7848 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-defdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6004-L6083 "src/map/status.cpp:6004–6083 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-build]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/CMakeLists.txt#L1-L54 "src/map/CMakeLists.txt:1–54 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-pcbase]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3754-L4994 "src/map/status.cpp:3754–4994 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-wrapper]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6525-L6743 "src/map/status.cpp:6525–6743 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-mobparse]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/mob.cpp#L5098-L5118 "src/map/mob.cpp:5098–5118 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-install]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L13183-L13235 "src/map/status.cpp:13183–13235 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-end]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L13430-L13520 "src/map/status.cpp:13430–13520 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-edges]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L5837-L5974 "src/map/status.cpp:5837–5974 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-str]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6752-L6818 "src/map/status.cpp:6752–6818 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-agi]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6827-L6891 "src/map/status.cpp:6827–6891 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-vit]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6900-L6952 "src/map/status.cpp:6900–6952 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-int]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6961-L7030 "src/map/status.cpp:6961–7030 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-dex]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7039-L7105 "src/map/status.cpp:7039–7105 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-luk]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7114-L7166 "src/map/status.cpp:7114–7166 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-misc]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L2600-L2765 "src/map/status.cpp:2600–2765 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-matkdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6283-L6372 "src/map/status.cpp:6283–6372 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-critdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6138-L6162 "src/map/status.cpp:6138–6162 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-regen]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L5264-L5369 "src/map/status.cpp:5264–5369 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-aspdbase]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L2355-L2416 "src/map/status.cpp:2355–2416 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-aspd]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L8237-L8364 "src/map/status.cpp:8237–8364 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-aspdfix]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L8376-L8395 "src/map/status.cpp:8376–8395 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-timingdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6375-L6449 "src/map/status.cpp:6375–6449 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-speed]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L8018-L8225 "src/map/status.cpp:8018–8225 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-equip]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3902-L4042 "src/map/status.cpp:3902–4042 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-scripts]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4044-L4214 "src/map/status.cpp:4044–4214 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-bonus]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.cpp#L3725-L4035 "src/map/pc.cpp:3725–4035 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-defdamage]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/battle.cpp#L4720-L4884 "src/map/battle.cpp:4720–4884 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-display]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.hpp#L1237-L1253 "src/map/pc.hpp:1237–1253 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-projection]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/clif.cpp#L3637-L3815 "src/map/clif.cpp:3637–3815 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-regenrate]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L5377-L5508 "src/map/status.cpp:5377–5508 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-regentick]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L15444-L15660 "src/map/status.cpp:15444–15660 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-traits]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7175-L7286 "src/map/status.cpp:7175–7286 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-traitdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6450-L6515 "src/map/status.cpp:6450–6515 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-traitbonus]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L8548-L8703 "src/map/status.cpp:8548–8703 at e985006171d2eb320ee512a653f4c83aea3d81b6"
