# Renewal status and derived-stat traceability matrix

Research baseline: Athena.NET `a3d006aa07a376d42c08187ea45cebc3c65384f4` (Epic 14 complete); rAthena **`e985006171d2eb320ee512a653f4c83aea3d81b6`**, verified in the clean local `legacy/rathena/` submodule. This audit changes documentation only. It is a research baseline and implementation gate, not an exhaustive audit, a claim of gameplay equivalence, or a set of executable reference fixtures. Untraced branches remain follow-up work.

The pinned implementation is the gameplay authority. Verified stock-iRO evidence governs serialization and observable protocol ordering; unresolved differences stay open. The baseline builds with `RENEWAL`, `RENEWAL_ASPD`, and `RENEWAL_STAT` enabled ([configuration][R-renewal]). Pre-Renewal branches are excluded from the proposed target. Custom imports and nondefault configuration must be explicit inputs, never assumed absent without declaring the scenario.

Read alongside the [dependency matrix](renewal-status-dependencies.md) and [implementation/verification plan](renewal-status-implementation-plan.md). References `[R-…]` link to exact pinned files and inclusive line ranges. Athena links are repository-relative and describe the audited commit, not a promised future implementation.

## Evidence and coverage conventions

- **Implemented** means the explicitly bounded behavior is present and inspected; it does not certify an entire system. Existing C# assertions are not independent reference execution.
- **Partial** means a basic path exists but a missing branch, input, arithmetic detail, or recalculation trigger affects the supported result.
- **Missing** means no authoritative implementation of that output was found.
- **Unverified** means evidence cannot establish equivalence, including numerical boundary behavior without running the source.
- **Deferred** means the reference mechanism is identified but its complete implementation belongs after the first Epic 15 slices or to later equipment/skill/combat work. Deferred inputs must not silently receive invented zero modifiers.

No complete derived-stat domain below qualifies as fully implemented. Bounded working pieces include immutable allocated-stat storage, single-step stat cost arithmetic, authoritative inventory-slot resolution, generated six-stat job-bonus arrays, the simple Increase AGI movement formula, and existing persistence/idempotency infrastructure. Their wider domains remain partial.

## Reconstructed calculation architecture

The call graph, not a list of candidate functions, determines order:

```text
pc_statusup / level-up / equipment / passive-skill change
  -> status_calc_pc macro (ALL bits, includes BASE)
  -> status_calc_bl_ [may defer for pending damage]
     -> status_calc_pc_ [save/restore script attachment]
        -> status_calc_pc_sub
           skill tree; reset accumulators/base fields
           equipment -> ammo -> combos -> capture param_equip
           cards -> random options -> status/bonus/pet scripts
           range/size -> cumulative job bonuses -> primary-stat passives
           allocated + equip + other bonus attributes
           weapon summaries -> max resources -> status_calc_misc
           rates -> derived-stat passives -> timing -> remaining bonuses
           copy rebuilt base status to battle status, preserving current resources
     -> status_calc_bl_main [temporary-status overlay and dependent bits]
     -> compare old/new battle state -> clif_updatestatus

status_change_start[_post_delay] / status_change_end
  -> status database CalcFlags (usually no BASE)
  -> status_calc_bl_
     -> status_calc_bl_main [uses existing base state; preserves prior battle fields]
     -> changed-value projection
```

Sources: [macro][R-macro], [wrapper][R-wrapper], [PC wrapper][R-pcwrap], [complete PC base pass][R-pcbase], [battle pass][R-main], [start/install][R-install], [end][R-endtail]. `status_calc_misc` runs **inside** the base pass, before rate/passive adjustments; it is not a final calculator after temporary effects. A fresh rebuild and a status-only delta can have different truncation history. A universal `sum all bonuses; compute every formula once` implementation would change behavior.

`status_calc_pc_sub` has a recursion guard (more than ten nested calculations returns `-1`), script execution and stateful dependencies; it is not a pure standalone function. Its wrapper preserves script attachment. The outer wrapper does not turn every early return into a client failure. Validate authoritative inputs before invoking an Athena calculator, and test reference failure paths rather than guessing how an invalid state should calculate ([reset][R-reset], [PC wrapper][R-pcwrap]).

## Shared arithmetic and data contract

These rules apply to every mechanic below unless a row specifies a narrower type or cap:

| Concern | Pinned behavior and implementation obligation |
|---|---|
| Types | `status_data` stores primary stats/traits and equipment ATK as `int16`; BATK as `int32`; per-hand ATK/ATK2/MATK/range, MATK endpoints and motions as `uint16`; HIT/FLEE/CRI/perfect dodge/soft defenses and trait substats as `int16`; current/max resources as `uint32`. Renewal hard DEF/MDEF use signed `int16` `defType`. [Types][R-types], [DEF type][R-deftype]. |
| Integer division | C++ integer division truncates toward zero, including negative temporary deltas. Preserve each intermediate division and conversion; mathematical floor or combining denominators is not an equivalent general replacement. |
| Floating point | Soft DEF/MDEF and ASPD explicitly use `float` subexpressions; max resources use `double`. C# `double` everywhere is not a faithful port. Record build/compiler/FP settings in the reference output. |
| Caps and narrowing | Caps occur at specific stages, not just at the final result. A helper returning `uint16` may be assigned to an `int16` field. Some rate/compound assignments narrow without another clamp. Model the pinned target's observed conversion behavior; do not silently substitute saturation. |
| Overflow limits | Signed intermediate overflow and out-of-range floating-to-integer conversions are not portable defined C++ arithmetic. In particular, MaxHP/SP cast `double` to `uint32` before their final unsigned cap. This audit does **not** specify invented wraparound for undefined cases. Establish legal reachable input ranges, capture compiler behavior for boundaries, and raise an explicit compatibility decision if a reachable state exceeds them. |
| No generic modifier bag | `param_equip` is captured before cards; flat HP/SP, equipment %, item %, status %, ASPD fixed/rate/additive and per-hand bonuses have different stages. Preserve origin, operation, order and applicable actor/job/weapon branches. |
| State versus display | Persisted allocated stats, rebuilt base stats, effective battle stats, combat roll components and client display components are different values. None should be overwritten with another merely because a field has a similar name. |

### Configuration and database provenance

| Input | Pinned defaults / provenance | Consequence |
|---|---|---|
| Actor masks | `enable_baseatk_renewal=0x29F` includes `BL_PC=1`, `BL_MOB=2`; `enable_critical=17`, `enable_perfect_flee=1`. [Battle config][R-confbattle], [actor bits][R-blocktypes]. | Monster base ATK is enabled; ordinary monsters do not acquire the PC CRI/perfect-dodge construction just because they have LUK. |
| Resources | HP/SP rates 100; max HP 330000 below level 100, 660000 below 151, 1100000 thereafter; max SP 1000000. [Player config][R-confplayer], [application][R-maxbase]. | Apply rates and level-specific caps after the PC max-resource helper. |
| Timing | max ASPD 190, third/extended/summoner 193; max walk speed 300; player damage-delay rate 100. [Player][R-confplayer], [battle][R-confbattle]. | `battle_adjust_conf` converts ASPD to delay `(2000-ASPD*10)*2`; walk 300 becomes `100*150/300=50` ms. Raw config numbers cannot be used as motion caps. [Conversion][R-confadjust], [job selection][R-aspdcap]. |
| Defense at attack time | `weapon_defense_type=0`; VIT penalty type 1, target PC, count 3, 5% per additional targeting count as coded. [Defaults][R-confbattle], [application][R-defdamage]. | A stat snapshot's soft/hard DEF is not the entire attack-specific defense calculation. |
| Regeneration | HP 6000 ms, SP 8000 ms, skill 10000 ms; Renewal natural-heal weight threshold defaults to 70 (the example is commented in player.conf; [Renewal loader default](https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/battle.cpp#L8388-L8396) must be preserved). [Player][R-confplayer], [rate/tick][R-regenrate] [R-regentick]. | Amount, eligibility, rates and elapsed-time state are separate. |
| Jobs | `JobDatabase` loads root `db/job_stats.yml`, its mode-specific imports (stats, ASPD, EXP, basepoints/outfits) and custom import. Parser accumulates job-level bonus increments into totals; tables use zero-based level indices. [Parser/loading][R-jobparse]. | Athena's generated one-based arrays need explicit index conversion. Job class/sex mapping for bonuses differs from direct class lookup for HP/SP. |
| Equipment | Root `db/size_fix.yml` is loaded first, then `db/re/size_fix.yml`, then custom import. Missing overlay values preserve an existing row. [Loader][R-sizeload], [root][R-sizedb], [Renewal overlay][R-sizere]. | Dagger is 100/75/50 for small/medium/large; absence from the RE overlay does not mean 100/100/100. |

Configuration imports and definitions must be hashed with golden fixtures. Defaults above describe the pinned baseline, not verified official iRO server configuration.

## Primary attributes

For each row, **origin/call chain** is `status_calc_pc → status_calc_bl_ → status_calc_pc_ → status_calc_pc_sub` for a rebuild, then `status_calc_bl_main → status_calc_<attribute>`; status-only changes enter at `status_calc_bl_` with DB flags. Input dependencies are allocated attribute, class/sex, job level, cumulative job bonuses, equipment/card/option/script accumulators, learned skills, job/rank/death state, active status entries and relevant equipment identity. [Assembly][R-jobstats], [propagation][R-edges].

Shared **formula/arithmetic**: rebuild the job/passive contribution; add allocated + `param_bonus` + `param_equip`; clamp `0..USHRT_MAX` before assignment to the signed field. Then apply the ordered temporary helper. Most helpers clamp `0..USHRT_MAX`, with early-return overrides. Harmonize, for example, returns early; LUK Curse returns zero. Concentrate's AGI/DEX percentage stage precedes subsequent flat additions and excludes cached bonus contributions; Quagmire suppresses it. Do not convert every modifier to a final percentage. The linked helper source defines each row's remaining modifier dependencies; not every status combination has been individually audited. Those branches require follow-up before enablement.

| Mechanic ID / mechanic | Exact reference function and source | Formula / modifier particulars | Existing Athena implementation; coverage | Test evidence and future work |
|---|---|---|---|---|
| RS-STR / STR | `status_calc_pc_sub`, `status_calc_str` [assembly][R-jobstats] [helper][R-str] | Job STR; Super Novice no-death bonus; Hilt Binding +1; ordinary PC Blessing +`val2`; all other ordered helper branches. Affects BATK. | `CharacterStatusEffectState.Recalculate` allocated + Blessing only; **Partial**. | `CharacterStatusEffectStateTests`, `MapClientSessionStatUpTests`: simple +10 and live plus. Add job/equipment/status-order/negative/cap cases and BATK propagation. **Epic 15.3 (partial, reference-verified):** Novice job-level bonus now included in effective six stats via `StatusCalculator.BasePrimaryStats` (fixture VF-02, `NoviceJobBonus_EffectiveSixStatsMatchReference`); other jobs, equipment/passives and unified snapshot pending. |
| RS-AGI / AGI | `status_calc_pc_sub`, `status_calc_agi` [assembly][R-jobstats] [helper][R-agi] | Job AGI; Super Novice; Concentrate; Increase AGI `2+val1`; other helper branches. Affects FLEE/soft DEF/ASPD/damage motion. | Same recalculator, allocated + Increase AGI only; **Partial**. | Existing +12/expiry tests omit job and dependent values. Add refresh rejection, Concentrate/Quagmire and timing/defense boundaries. **Epic 15.3 (partial, reference-verified):** Novice job-level bonus now included in effective six stats via `StatusCalculator.BasePrimaryStats` (fixture VF-02, `NoviceJobBonus_EffectiveSixStatsMatchReference`); other jobs, equipment/passives and unified snapshot pending. |
| RS-VIT / VIT | `status_calc_pc_sub`, `status_calc_vit` [assembly][R-jobstats] [helper][R-vit] | Job VIT; Super Novice; equipment and helper branches; affects HP, DEF2, MDEF2, regen. | Effective stats return allocated VIT; progression alone adds job VIT; **Partial, inconsistent consumers**. | `CharacterProgressionServiceTests` includes job VIT. Add allocation/equipment changes at fixed levels and capped HP/current-HP transition. **Epic 15.3 (partial, reference-verified):** Novice job-level bonus now included in effective six stats via `StatusCalculator.BasePrimaryStats` (fixture VF-02, `NoviceJobBonus_EffectiveSixStatsMatchReference`); other jobs, equipment/passives and unified snapshot pending. |
| RS-INT / INT | `status_calc_pc_sub`, `status_calc_int` [assembly][R-jobstats] [helper][R-int] | Job INT; Dragonology `(level+1)/2`, Research Trap, Power of Land; Super Novice; Blessing; helper branches. Affects MATK, MDEF2, SP, regen. | Recalculator allocated + Blessing; progression allocated + job INT without Blessing; **Partial**. | Existing buff and progression tests cover separate paths. Add simultaneous job/buff/equip and MaxSP/regen transition checks. **Epic 15.3 (partial, reference-verified):** Novice job-level bonus now included in effective six stats via `StatusCalculator.BasePrimaryStats` (fixture VF-02, `NoviceJobBonus_EffectiveSixStatsMatchReference`); other jobs, equipment/passives and unified snapshot pending. |
| RS-DEX / DEX | `status_calc_pc_sub`, `status_calc_dex` [assembly][R-jobstats] [helper][R-dex] | Job DEX; Owl's Eye; Super Novice; Concentrate then Blessing and other statuses. Affects BATK, HIT, MATK, MDEF2, ASPD. | Allocated + Blessing; **Partial**. | Existing primary packet tests; add all dependent outputs and ranged BATK/ASPD branch. **Epic 15.3 (partial, reference-verified):** Novice job-level bonus now included in effective six stats via `StatusCalculator.BasePrimaryStats` (fixture VF-02, `NoviceJobBonus_EffectiveSixStatsMatchReference`); other jobs, equipment/passives and unified snapshot pending. |
| RS-LUK / LUK | `status_calc_pc_sub`, `status_calc_luk` [assembly][R-jobstats] [helper][R-luk] | Job LUK; Super Novice; Curse zero override and other helper branches. Affects BATK, MATK, HIT, FLEE, CRI, perfect dodge. | Allocated only; **Partial**. | Stat-up tests verify allocated value/cost, not derived effects. Add divisions at 3/5/10 boundaries, Curse and status restoration. **Epic 15.3 (partial, reference-verified):** Novice job-level bonus now included in effective six stats via `StatusCalculator.BasePrimaryStats` (fixture VF-02, `NoviceJobBonus_EffectiveSixStatsMatchReference`); other jobs, equipment/passives and unified snapshot pending. |

Shared Athena paths: [state](../../src/MapServer/World/CharacterGameplayState.cs), [effect state](../../src/MapServer/World/CharacterStatusEffects.cs), [progression](../../src/MapServer/World/CharacterProgressionService.cs). The six rows' complete test scenarios and recalculation triggers are expanded in the [dependency matrix](renewal-status-dependencies.md). Their supported Epic 15 starting scope is the generated job data plus ordinary, explicitly modeled character inputs; unsupported modifiers must remain visible until implemented.

### RS-JOB — job and allocated-stat separation / stat allocation

| Field | Specification |
|---|---|
| Reference function / source | `JobDatabase::parseBodyNode`/`loadingFinished`, `status_calc_pc_sub`, `pc_need_status_point`, `pc_maxparameterincrease`, `pc_statusup`: [job parser][R-jobparse], [assembly][R-jobstats], [allocation][R-statup]. |
| Call chain | Load job increments → accumulate per level → `status_calc_pc_sub` adds all twelve job bonuses at `job_level-1`; allocation validates intent → changes allocated stat/points → full `status_calc_pc` → cost/points/ack projection. |
| Input dependencies | Server-owned allocated stats, points, job/class/sex, job level, generated caps, requested stat/amount. No client-reported current stat or balance is authoritative. |
| Formula / modifiers / arithmetic | Single point cost at current `s`: `s<100 ? 2+(s-1)/10 : 16+4*((s-100)/5)`; sum per step. Base-stat allocation caps are separate from effective-stat caps. Reference clamps requested amount to affordable maximum, then validates maximum parameter, cost and remaining points; does not deduct on rejection. |
| Existing Athena / coverage | `CharacterStatService` and `CharacterGameplayStateSession.IncreaseStatAsync`: correct inspected positive single-step cost and persistence-before-state-before-notify; **Partial** domain. Generated six-stat bonuses exist but are not uniformly consumed. Multi-step requests are rejected as a whole when unaffordable, unlike reference partial acceptance. |
| Test evidence | `CharacterStatServiceTests`, `CharacterGameplayStateSessionTests`, `MapClientSessionStatUpTests` exercise invalid enum/points/caps/persistence failures and the six captured 2→3 requests. They do not prove the full reference cap/amount behavior or derived outputs. |
| Future work | Preserve single authoritative durable transition; add job bonuses to every calculation consumer, cumulative-array indexing tests, multi-step reference fixtures and a separately reviewed iRO rejection/amount policy. No new failure packet without iRO evidence. |

## Derived-stat matrix

In the tables below `B` denotes rebuilt base state, `E` effective battle state, `L` Base Level; `trunc` means the actual conversion toward zero at the indicated point. The shared arithmetic contract and linked source branches apply. The [dependency document](renewal-status-dependencies.md) supplies exact triggers and ordered propagation for every row.

### RS-BATK — base/status ATK

| Field | Specification |
|---|---|
| Reference function / source | `status_base_atk`, `status_calc_misc`, `status_calc_batk`, `status_calc_bl_main`: [base][R-baseatk], [misc][R-misc], [overlay][R-batk], [delta][R-atkdelta]. |
| Call chain | PC base rebuild → misc → base ATK; battle pass BATK → recompute with effective attributes + `B.batk - base_atk(B)` → status helper. Mob DB loading also calls misc. |
| Input dependencies | Actor type and enable mask, level, STR/DEX/LUK/POW, weapon family, base accumulator, active statuses. |
| Formula | PC: choose `main=STR, other=DEX`, swap for bow/musical/whip/firearms; `(main*10 + other*10/5 + LUK*10/3 + L*10/4)/10 + 5*POW`. Ordinary Renewal non-PC path: `STR+L`. |
| Modifier dependencies | Base enable mask; flat/rate helper order includes Volcano, Ash, Pyroclastic, Angriffs Modus, Enervation, Zangetsu, Loud (+30 Renewal), Nibelungen (+20%), Sun stance, Intensive Aim (+150). Item `bBaseAtk` goes to **equipment ATK**, not this field, under Renewal. [Bonus dispatch][R-bonus]. |
| Arithmetic semantics | Each integer division is retained. Base helper clamps `0..65535`; overlay BATK is `int32` and has no universal final clamp. Battle pass only enters if `B.batk` is nonzero. |
| Existing Athena / coverage | `WeaponAttackCalculator.BaseAttack` implements a POW=0 melee PC expression for damage; no general BATK snapshot; mob calculator omits BATK. **Partial**. **Epic 15.2b (reference-verified):** `MobBasicAttackCalculator` now adds mob `batk = str + level` and uses the exclusive `[atkmin, atkmax)` roll; fixture VF-14 (mobs 1002/1031/1013 via real `battle_calc_attack`, `MobAttack_ReachableDamageValuesMatchReference`). Accuracy/FLEE, crit, elements, cards and armor DEF remain unmodeled. **Epic 15.4 (partial, reference-verified):** `DerivedStatCalculator` computes PC BATK for the no-equipment/POW=0 scope incl. Blessing STR/DEX delta (fixtures VF-01/02/07/08). Equipment, ranged weapons and passives unsupported. |
| Test evidence | `WeaponAttackCalculatorTests` hand-selected novice values; no ranged swap, job, POW, disabled mask, zero guard or extreme arithmetic fixtures. |
| Future work | Expose status ATK once; use it from combat and display without confusing PC damage's multiplier with displayed BATK. Include RS-MOB corrections. |

### RS-WATK — weapon and equipment ATK components

| Field | Specification |
|---|---|
| Reference function / source | `status_calc_pc_sub`, `status_weapon_atk`, `pc_bonus`, `status_calc_watk`: [equipment][R-equip], [summaries][R-weaponsum], [bonus][R-bonus], [overlay][R-watk], [battle update][R-atkdelta]. |
| Call chain | Authoritative equipped slots → item/refine/grade DB + scripts → per-hand base `rhw/lhw` → `watk/watk2` display summaries and separate `eatk` → temporary helper. Damage later selects its own component/variance/size path. |
| Input dependencies | Item instance identity, item definition, slot/hand, weapon family/level, refine/grade, cards/options/combos, ammo, map restrictions, permitted equipment, skills/statuses. |
| Formula | Per-hand item ATK plus refine bonus `/100`; `status_weapon_atk` sums `wa.atk+wa.atk2` in `uint32`, assigned to `uint16` summary. Equipment ATK is separate. Refine over-attack randomness and size are attack-time components, not one added displayed constant. |
| Modifier dependencies | Item refine/grade rules (level-5 weapons also PATK/SMATK); `pc_bonus` hand/ammo context; ordered status helper; dual-hand asymmetry: Renewal update sets right to zero and updates left when `B.watk==0`, otherwise updates right only. Preserve/verify this source quirk rather than “symmetrizing” it. |
| Arithmetic semantics | Refine scaling and grade operations occur before storage; summary narrowing and helper clamp `0..65535`; per-component attack-time divisions retain order. |
| Existing Athena / coverage | `EquippedWeaponResolver` returns right-hand definition; `WeaponAttackCalculator` supports unrefined basic melee using item ATK, STR and weapon level. Inventory has refinement but resolver discards it. Armor/cards/ammo/dual-hand/refine bonuses absent. **Partial**. |
| Test evidence | Resolver tests cover none/unknown/wrong type; weapon tests cover Knife ATK 17. Their 100% size assumption is disproved by root size DB (RS-SIZE). No oracle for numeric variance/float endpoints. |
| Future work | Preserve per-hand, weapon, equipment and refine components; add data only with a concrete consumer. Full card scripting, combat mastery/element/cardfix and all weapon combat branches remain later work; do not label their unsupported states equivalent. |

### RS-MATK — magic attack and display components

| Field | Specification |
|---|---|
| Reference function / source | `status_base_matk_min/max`, `status_calc_bl_main`, `status_calc_pseudobuff_matk`, `status_calc_consumablematk`: [base][R-basematk], [battle path][R-matkdelta], [helpers][R-matkbonus], [display macros][R-display]. |
| Call chain | Misc base → base rates → battle MATK full construction; client display uses separate `pc_leftside_matk/pc_rightside_matk`. |
| Input dependencies | Effective INT/DEX/LUK/SPL, level, both weapon MATKs/levels/refines, soul balls, equipment/hidden MATK, learned skills, statuses, MATK rate. |
| Formula | PC base min=max `INT + INT/2 + DEX/5 + LUK/3 + L/4 + 5*SPL`. Battle path adds soul-ball contribution, weapon MATK and per-hand weapon-level variance, overrefine interval, Magic Power, equipment/hidden additions, pseudo-buff then consumable helpers and equipment total rate **in source order**. |
| Modifier dependencies | Pseudo-buff includes NV_TRANSCENDENCE `15*lv+(lv>4?25:0)` and its ordered statuses. Consumable helper is currently clamp-only in this pin, not an invitation to invent effects. Bow refinement MATK exclusion and separate weapon variance matter. |
| Arithmetic semantics | Base helper returns `uint16`; each pseudo/consumable stage clamps to unsigned16; final min/max clamp `0..65535`. Weapon variance uses integer `/10`. Preserve branch-specific intermediate caps. Displayed left/right are not battle min/max. |
| Existing Athena / coverage | No derived MATK calculation found; `ItemDefinition` does not carry weapon MATK. **Missing**. |
| Test evidence | Primary INT/DEX/LUK packet tests do not cover MATK. Need base and per-hand variance, intermediate saturation, Magic Power, job/passive, display-vs-combat fixtures. |
| Future work | Compute base MATK for the declared initial scope, retain separate displayed/effective components; defer spell-damage resolution and unsupported gear/skills explicitly. |

### RS-DEF — hard DEF

| Field | Specification |
|---|---|
| Reference function / source | `status_calc_pc_sub`, `pc_bonus`, `status_calc_def`: [equipment][R-equip], [scripts/refine rounding][R-scripts], [rates][R-rates], [helper][R-def]. |
| Call chain | Item DEF accumulation → aggregate armor refine `(refinedef+50)/100` → scripts/bonus contributions → base DEF rate → battle DEF helper. |
| Input dependencies | Every unique equipped item, armor/refine data, script modifiers, status overrides, map/equipment permission. |
| Formula / modifier dependencies | Sum real item DEF and script/refine contributions in source stages. Berserk/Eternal Chaos return zero; Keeping returns 90; Nyanggrass and DEFSET are overrides; ordinary flat/% modifiers follow the helper order. Steel Body's Pre-Renewal 90 DEF branch is excluded. |
| Arithmetic semantics | Signed Renewal `defType`; helper clamps `DEFTYPE_MIN..DEFTYPE_MAX`; negative DEF is possible. Defense during an attack uses additional reductions and a `-400 → -399` denominator guard ([attack defense][R-defdamage]). |
| Existing Athena / coverage | `MobBasicAttackCalculator` assumes hard DEF zero; armor definitions lack DEF/refine-level inputs. Equipment mutation exists, so “fresh naked novice” cannot describe every character the caller accepts. **Missing**. |
| Test evidence | Mob tests explicitly exercise zero armor only; no equipped-defense test. |
| Future work | Add authoritative armor data and total DEF, route it into `PlayerCombatSnapshot`; preserve attack-time effects as a separate later combat dependency. Never persist a placeholder zero as an equipment result. |

### RS-DEF2 — soft DEF

| Field | Specification |
|---|---|
| Reference function / source | `status_calc_misc`, `status_calc_bl_main`, `status_calc_def2`: [base][R-misc], [delta][R-defdelta], [helper][R-def2]. |
| Call chain | Misc → base DEF2 percentage → battle delta from effective VIT/AGI → status helper. |
| Input dependencies | Level, base/effective VIT and AGI, base bonus/rate, active statuses. |
| Formula | PC base addition `trunc(((float)L+VIT)/2 + (float)AGI/5)`. Battle adjustment adds `trunc(((float)E.VIT/2 - (float)B.VIT/2) + ((float)E.AGI/5 - (float)B.AGI/5))` to existing `B.def2`, then helper. Both base and effective operands are explicitly converted to float before division; the combined delta is truncated once. |
| Modifier dependencies | Base def2_rate first; Berserk/Chaos/DEFSET overrides; Angelus uses `VIT/2 * val2/100`; poison-family and other ordered effects. |
| Arithmetic semantics | Base cap `0..SHRT_MAX`; helper signed cap `SHRT_MIN..SHRT_MAX`. Delta truncation is not equivalent to recalculating a single effective-stat expression. Preserve the precise operand casts in source. |
| Existing Athena / coverage | Mob calculator computes a fresh formula using effective VIT/AGI, with no stored base/rates/status overlay. **Partial**. **Epic 15.4 (partial, reference-verified):** `DerivedStatCalculator` now owns soft DEF (base + VIT/AGI delta path, VF-01/02/07/08); `MobBasicAttackCalculator` consumes `PlayerCombatSnapshot.SoftDefense` instead of its own formula. |
| Test evidence | `Calculate_Def2Formula_MatchesExactPinnedTruncationAtAgiBoundary` uses an even level+VIT sum; it cannot distinguish several wrong split-rounding formulas. Need odd sums, simultaneous VIT/AGI changes, negative expiry deltas and base-rate combinations. |
| Future work | One soft-DEF output from staged calculation; combat consumes it then applies attack-context defense rules. |

### RS-MDEF — hard MDEF

| Field | Specification |
|---|---|
| Reference function / source | `pc_bonus`, `status_calc_pc_sub`, `status_calc_mdef`: [bonus][R-bonus], [rates][R-rates], [helper][R-mdef]. |
| Call chain | Equipment/card/script MDEF accumulator → base rate → temporary helper. |
| Input dependencies | Equipped bonuses, relevant skills (including Endure-related contributions), status state and base rates. |
| Formula / modifiers | No invented armor-MDEF constant: use actual script/bonus sources. Berserk zero, Nyanggrass, MDEFSET overrides precede Endure and other flat/% branches. |
| Arithmetic semantics | Signed Renewal `defType`, source-stage arithmetic and final signed clamp. |
| Existing Athena / coverage | No authoritative total or magic-defense consumer; **Missing**. |
| Test evidence | No independent hard-MDEF fixture; current wire/primary tests do not establish it. |
| Future work | Snapshot field and source-backed modifiers when supported; magic damage remains outside initial Epic 15. |

### RS-MDEF2 — soft MDEF

| Field | Specification |
|---|---|
| Reference function / source | `status_calc_misc`, `status_calc_bl_main`, `status_calc_mdef2`: [base][R-misc], [delta][R-defdelta], [helper][R-mdef2]. |
| Call chain | Misc → base MDEF2 rate → INT/DEX/VIT temporary delta → helper. |
| Input dependencies | Level and base/effective INT/DEX/VIT, bonuses/rates/statuses. |
| Formula | PC base addition `trunc(INT + (float)L/4 + (float)(DEX+VIT)/5)`; overlay `E.INT-B.INT + trunc(((float)E.DEX/5 - (float)B.DEX/5) + ((float)E.VIT/5 - (float)B.VIT/5))` added to `B.mdef2`. Both base-side and effective-side divisions are floating point; preserve the single cast of their combined delta. |
| Modifier dependencies | Berserk zero, Eska 90, MDEFSET override; Mind Breaker, Burning and Analyze rates in order. |
| Arithmetic semantics | Base cap nonnegative signed16; temporary helper signed16 limits; base expression groups DEX+VIT before division, overlay does not. |
| Existing Athena / coverage | **Missing**. **Epic 15.4 (partial, reference-verified):** soft MDEF in `DerivedStatCalculator` (base + INT/DEX/VIT deltas; VF-01/02/07/08); no combat consumer yet. |
| Test evidence | Need grouped versus separate `/5`, level `/4`, expiry negative deltas, overrides and rate-order fixtures. |
| Future work | Base/effective distinct representation; do not infer client field names from internal `mdef2` (see synchronization document). |

### RS-HIT — HIT

| Field | Specification |
|---|---|
| Reference function / source | `status_calc_misc`, `status_calc_pc_sub`, `status_calc_hit`, `status_calc_bl_main`: [base][R-misc], [rates/passives][R-rates], [helper][R-hit], [delta][R-defdelta]. |
| Call chain | Misc + base rate + passive additions → battle attribute delta → HIT helper. |
| Input dependencies | Level, DEX/LUK/CON, hit accumulator/rate, weapon/passive skills, statuses. |
| Formula | PC base adds `L+DEX+LUK/3+175+2*CON`; battle adds `E.DEX-B.DEX + E.LUK/3-B.LUK/3 + 2*(E.CON-B.CON)`. |
| Modifier dependencies | Weapon Research, Single Action and other passive branches in base pass; helper includes Blessing **additional `2*val1` HIT** independently of its DEX increase. Blind and ordered modifiers also apply. |
| Arithmetic semantics | Base/helper clamp `1..SHRT_MAX`; rate/passive order may differ from moving them into the final helper. |
| Existing Athena / coverage | Effective DEX exists; HIT and attack accuracy do not. **Missing**. **Epic 15.4 (partial, reference-verified):** HIT in `DerivedStatCalculator` incl. Blessing `val1*2` (VF-01/02/07/08); accuracy roll itself still unmodeled (Epic 17). |
| Test evidence | Blessing tests assert only primary +10; cannot catch missing extra HIT. |
| Future work | Compute snapshot HIT now; separately stage accuracy integration with independent attack fixtures (not a hidden expansion into full combat). |

### RS-FLEE — FLEE

| Field | Specification |
|---|---|
| Reference function / source | `status_calc_misc`, `status_calc_pc_sub`, `status_calc_flee`, `status_calc_bl_main`: [base][R-misc], [rates/passives][R-rates], [helper][R-flee], [delta][R-defdelta]. |
| Call chain | Base construction/rate/passives → AGI/LUK/CON deltas → map penalties and status helper. |
| Input dependencies | Level, AGI/LUK/CON, bonuses, learned skills, weapon/job state, map GvG/BG flags and rates, statuses. |
| Formula | PC base adds `L+AGI+LUK/5+100+2*CON`; overlay adds AGI difference, difference of divided LUK terms, twice CON difference. |
| Modifier dependencies | Improve Dodge and class/passive branches; GvG/BG penalty before empty-SC return; Poison Mist zero and Overed Boost override; ordered flat/% effects. |
| Arithmetic semantics | `1..SHRT_MAX` normal helper cap, but early overrides have their own return behavior. |
| Existing Athena / coverage | **Missing**; miss results currently come from damage below one, not HIT/FLEE accuracy. **Epic 15.4 (partial, reference-verified):** FLEE in `DerivedStatCalculator` incl. Increase AGI via AGI delta (VF-01/02/07/08); map GvG/BG penalties and the FLEE accuracy roll unmodeled. |
| Test evidence | Existing zero-damage tests are not FLEE tests. Need map penalty, override, `/5`, buff refresh/end and negative delta cases. |
| Future work | Derived FLEE first; accuracy and crowd penalties require attack-context inputs and later review. |

### RS-PD / RS-CRI — perfect dodge and critical rate

| Field | Perfect dodge (RS-PD) | CRI (RS-CRI) |
|---|---|---|
| Reference function / source | `status_calc_misc`, `status_calc_flee2` [base][R-misc] [helper][R-flee2] [delta][R-critdelta] | `status_calc_misc`, `status_calc_critical` [base][R-misc] [helper][R-cri] [delta][R-critdelta] |
| Call chain | Enabled actor misc → rates → LUK delta → helper | Enabled actor misc → rates/passives → LUK delta → helper → Katar doubling |
| Input dependencies | Actor mask, LUK, bonuses/rates/statuses | Actor mask, LUK, level, weapon family, bonuses/rates/passives/statuses |
| Formula | PC base adds `LUK+10`; overlay adds LUK delta | Base adds `10+3*LUK+L/10`; overlay adds `3*(E.LUK-B.LUK)` |
| Modifier dependencies | Whistle, Unlucky, Hiss, Doram flee; helper order | Explosion Spirits, Fortune, True Sight, Cloaking double, quickens and other ordered helper effects; Katar final double |
| Arithmetic semantics | Internal tenths of percent; base cap `0..32767`, helper `10..32767`; delta guarded by nonzero base | Internal tenths of percent; base minimum 1, helper minimum 10; post-helper Katar multiplication/narrowing is not covered by that clamp |
| Existing Athena / coverage | **Missing** | **Missing** **Epic 15.4 (partial, reference-verified):** base CRI and perfect dodge (flee2) in `DerivedStatCalculator`, no overlay from supported statuses (VF-01/02/07/08); no combat consumer yet. |
| Test evidence | No independent dodge-rate fixtures; add mask/zero/min/cap and display `/10` | No independent CRI fixtures; add level10, LUK increments, Katar and cap/overflow boundaries |
| Future work | Calculate field, defer dodge random resolution explicitly | Calculate field, defer crit chance/damage pipeline explicitly |

### RS-ASPD — ASPD, attack motion and repeat delay

| Field | Specification |
|---|---|
| Reference function / source | `status_base_amotion_pc`, `status_calc_aspd`, `status_calc_fix_aspd`, `pc_maxaspd`, `battle_adjust_conf`: [base][R-aspdbase], [statuses][R-aspd], [fixed delay][R-aspdfix], [battle stage][R-timingdelta], [job cap][R-aspdcap], [config conversion][R-confadjust]. |
| Call chain | Job ASPD lookup + weapon/hand/shield + attribute/passive/fixed-SC expression → base or battle continuation → configured job delay clamp → `adelay=2*amotion`. |
| Input dependencies | Canonical job ASPD data, both hands/shield, effective AGI/DEX, learned skills, riding/dragon state, casting/Free Cast, all three ASPD modifier forms, active statuses, class-specific config. |
| Formula | `float t = DEX*DEX/(ranged?7.0f:5.0f) + AGI*AGI*0.5f`; then `t=(float)(sqrt(t)*0.25f)+196`; ASPD=`(int32)(t + (float)(fixedSC+skillVal)*AGI/200)-min(jobAspd,200)`. Shield adds its DB slot; left weapon can add its job value divided by four. Battle path multiplies by base `aspd_rate/1000`; handles Free Cast; adds `max(195-aspd,2)*(aspd_rate2+nonfixedSC)/100`; converts to `2000-10*aspd`, adds `aspd_add`, applies fixed-delay helper, clamps `pc_maxaspd/2..8000/2`. |
| Modifier dependencies | Advanced Book/Devil/Single Action, riding penalties; fixed quicken/status maxima and potion selection; nonfixed effects include Increase AGI `val1`; passive musical lesson/plagiarism occur after the helper's empty-SC early return. This surprising gate requires a fixture. |
| Arithmetic semantics | Preserve `float` assignment and cast before subtraction, per-stage integer math, signed/narrow fields. Missing job lookup returns zero in Renewal base helper; do not invent Novice data for an unknown job. |
| Existing Athena / coverage | `AttackDelayCalculator`: Novice fist40/dagger55 for every caller; throws for other weapon types; doubles instead of reference floats; missing job/hand/skill/rate inputs. **Partial with confirmed cap-unit error**: clamps to 95 ms motion using raw 190; configured reference lower motion is 100 ms (200 ms repeat delay). Third/extended/summoner normally 70/140 ms. **Epic 15.5 (reference-verified, Novice fist only):** `AttackDelayMs` matches `adelay` for VF-02 (12 allocations incl. job bonuses) and every VF-07/08 step/post-end state incl. Increase AGI (`AttackAndWalkTiming` tests). Other jobs/weapons, shields/dual-wield and ASPD potions remain unsupported. **Open (needs approval, Orleans contract):** attack animation `SrcSpeed` is still the hardcoded 460 in `MapClientSession` (2 sites) and `MapTcpServer` feed replay; the reference value is the attacker's `amotion` (440 for a fresh Novice) and other replicas cannot derive it without a `PlayerAttackAction` feed-contract field. |
| Test evidence | Combat timing tests cover a few fresh-Novice intervals. `PendingMonsterDamageAttemptTests` checks frozen retry cadence but obtains numeric expectations from the same calculator. No independent float/cap/job fixtures. |
| Future work | Generate/import correct job ASPD, preserve motion versus interval units, converge combat scheduler and visible animation source. No unsupported weapon fallback. |

### RS-DMOTION — player damage motion

| Field | Specification |
|---|---|
| Reference function / source | `status_calc_pc_sub`, `status_calc_bl_main`: [base timing][R-timingbase], [battle timing][R-timingdelta]. |
| Call chain | Base rebuild computes from base AGI; temporary AGI sets DSPD and may recompute from effective AGI. |
| Input dependencies | Base/effective AGI, `player_damage_delay_rate`; preexisting battle state on delta updates. |
| Formula / modifiers | `clamp(800-4*AGI,400,800)` then configured percentage. |
| Arithmetic semantics | Integer formula, config applied after clamp, stored unsigned16. Battle DSPD branch assigns only when base AGI differs from battle AGI. On last AGI buff expiration they may become equal, leaving previous dmotion unless another rebuild intervenes: **source-observed quirk; reference-run question**, not permission to silently normalize behavior. |
| Existing Athena / coverage | `PlayerDamageMotionCalculator` matches formula at default rate within ordinary input range, but no config/delta-history behavior. **Partial** overall. **Epic 15.5 (reference-verified):** `PlayerDamageMotionCalculator` matches `dmotion` for all VF-02 cases and VF-07/08 steps (AGI-effective). |
| Test evidence | Integration packets check motions, not the reference status-only restoration quirk or nondefault rate. |
| Future work | Add transition fixtures, keep animation damage motion separate from attack interval and movement speed. |

### RS-SPEED — movement speed

| Field | Specification |
|---|---|
| Reference function / source | `status_calc_pc_sub`, `status_calc_speed`, `status_calc_bl_main`: [base initialization][R-reset], [helper][R-speed], [wrapper stage][R-speeddelta]. |
| Call chain | Base speed/permanent override → battle SPEED marks rewalk → helper → configured minimum walking delay → pet propagation. |
| Input dependencies | Base/permanent speed, riding/Mado, casting/skills, cart/weight, all speed statuses, equipment speed forms, config. AGI itself is not a general movement-speed input. |
| Formula / modifiers | Helper has permanent/Mado/casting branches; mount haste, maximum selected slow, maximum selected haste (Increase AGI 25), rate floor 40, cart/paralysis modifiers, apply rate, Steel Body/Defender/armor limits, Walkspeed division. See ordered source; bonuses are not all additive. |
| Arithmetic semantics | Integer rates and stage order; helper clamp 20..1000, then player configured floor (default 50) unless permanent-speed behavior bypasses it. |
| Existing Athena / coverage | `MovementSpeedCalculator` implements base150 with Increase AGI25 and floor40/clamp20..1000; matches that isolated branch. No general job/cart/casting/config state. **Partial**. **Epic 15.5 (reference-verified):** `MovementSpeedCalculator.CellDurationMs` matches `speed` for VF-02 and VF-07/08 steps incl. Increase AGI haste and its removal. |
| Test evidence | `MovementSpeedCalculatorTests`, movement/expiration integration cover the single buff. Add active-walk refresh/end, slow/haste competition and override/config fixtures. |
| Future work | One calculated delay passed into movement scheduling and public presence projection; no distributed call from calculator. |

### RS-HP / RS-SP — maximum resources

| Field | MaxHP (RS-HP) | MaxSP (RS-SP) |
|---|---|---|
| Reference function / source | `status_calc_maxhp_pc`, `status_get_hpbonus*` [helper][R-maxhp] [bonuses][R-hpbonus] | `status_calc_maxsp_pc`, `status_get_spbonus*` [helper][R-maxsp] [bonuses][R-spbonus] |
| Call chain | Job table → base helper with base VIT → wrapper rates/cap; battle MAXHP repeats helper with effective VIT → clamp current HP downward | Same using INT and MAXSP/current SP |
| Input dependencies | Job class/level/base-HP table, VIT, trans/primary-fourth/ranker, equip-only VIT, fixed/equipment/item/status bonuses, global rate/cap | SP table, INT, same class/rank factors, equip-only INT, SP bonus groups/rate/cap |
| Formula | `double d=job.base_hp[max(L,1)-1]`; if VIT>0 `d*=1+VIT*.01`; upper/primary-fourth `*1.25`, else Taekwon ranker `*3`; add equip VIT and fixed HP; compute equip% and item% from the **same d**, add both; then `d+=(int64)(d*statusRate/100)`; minimum1; cast unsigned32 | Identical stage shape with SP/INT; not a shared final multiplier that combines rates |
| Modifier dependencies | Trust, Sprite Mable, Power of Sea/Sea skills, Breakthrough/Transcendence, flat/status bonuses; equipment and item buckets distinct (HP item helper currently returns0); rates sum within their category | Kaina/Research Trap/Lesson/Sprite/Sea, Meditatio/Soul Drain/sex-dependent lesson and status rates; Energy Drink item bucket |
| Arithmetic semantics | Double then int64 rate truncation then uint32; wrapper uses `hp_rate*(max/100.)`, then level caps; see overflow caveat above | Same; SP global cap, independently of HP level caps |
| Existing Athena / coverage | `CharacterProgressionService.CalculateMaximum` integer `base*(100+allocated+job)/100`, only on level changes; no active modifiers/equip/class factor/global cap. **Partial** | Same; Blessing INT never updates MaxSP. **Partial** **Epic 15.4 (partial, reference-verified):** `StatusCalculator.MaxHp/MaxSp` (double `table*(1+stat*0.01)`, truncated) is now the single owner (the old integer `CalculateMaximum` agreed with the reference on all 40 VF-04 cases and was replaced to remove the duplicate formula); VIT/INT stat-ups recalc maxima in the same durable write with clamp-only current (VF-11, `IncreaseStatAsync`). Not yet: x1.25 upper/fourth and Taekwon-ranker multipliers, equipment/skill HP/SP bonuses, client 0x00B0 max-HP resend after stat-up (iRO evidence needed, Stage 15.7), job-bonus VIT/INT in the stat-up path (only job level 1 fixtures). |
| Test evidence | Novice base-level HP, job-VIT cases; no oracle and no rounding-sensitive double cases | Novice SP level test; no buff/gear/job/class/rounding oracle |
| Future work | Reuse generated base tables but centralize calculation; VIT/stat/equip/effect transitions recalc and atomically clamp current HP. Base-level healing remains an explicit progression event | INT transitions recalc; expiry clamps current SP if needed, does not heal on activation unless a separate source action calls for it |

Shared source: [table fallback][R-hpjob], [base resource caps][R-maxbase], [battle resource caps/clamping][R-maxdelta]. Fallback HP uses `35+floor(L*hp_increase/100)` plus individually rounded factor increments (and Summoner/Super Novice branches); SP uses its separate Ninja/Gunslinger/Summoner cases. `CharacterDataCompiler` emits six job-bonus arrays and tables but its fallback is not a substitute for validating every pinned table/import/class branch. In particular, compiler comments relying on dense Ninja/Gunslinger SP tables need fixture coverage before claiming all-job fallback equivalence.

### RS-REGEN — HP/SP regeneration amounts, rates and ticking

| Field | Specification |
|---|---|
| Reference function / source | `status_calc_regen`, `status_calc_regen_rate`, `status_natural_heal`: [amounts][R-regen], [rates/eligibility][R-regenrate], [timer][R-regentick]. |
| Call chain | Misc or battle VIT/MAXHP/INT/MAXSP flags → amounts; SCB_REGEN → rates separately; timer processes current amounts/rates/flags and elapsed accumulators. |
| Input dependencies | Effective VIT/INT/max HP/SP, recovery bonuses, learned recovery/meditation/sitting skills, sitting/walking/dead/hidden/overweight/map states, statuses, timer history/config. |
| Formula | Natural HP `VIT/5+max(1,maxHP/200)`; SP `1+INT/6+maxSP/100`, with INT≥120 adding `(INT-120)/2+4`; recovery rates then PC cap. Meditatio modifies SP; HP recovery skill adds `5*lv+lv*maxHP/500`; SP recovery and sitting skills have their own formulas and rates in the amount helper. |
| Modifier dependencies | Separate natural/skill/sitting channels; Magnificat/Regeneration/Revitalize/Tension Relax and suppression flags; dead/hidden, weight thresholds, walking permission, sitting/castle accelerations; tick logic handles skill accumulators differently from natural ticks. |
| Arithmetic semantics | Integer division, caps typically 0..32767 for PC amount; channel-specific minima after passives. Natural tick uses interval/half-timer tolerance and resets; do not replace it with unlimited catch-up healing. |
| Existing Athena / coverage | Healing service exists for explicit healing, not a reference natural-regeneration engine. **Missing**. |
| Test evidence | `CharacterHealServiceTests` prove explicit healing clamps only. Need amount/rate/timing fixtures, INT119/120/121/122, HP199/200, SP99/100, walking/weight/sitting and simultaneous suppression. |
| Future work | Epic 15 calculates amounts and models rate/eligibility dependencies; activate periodic healing only in a separate reviewed stage with persistence/cadence semantics. Never claim regen support from amounts alone. |

### RS-RANGE / RS-SIZE — equipment range and attack-size integration

| Field | Range (RS-RANGE) | Size modifiers (RS-SIZE) |
|---|---|---|
| Reference function / source | `status_calc_pc_sub`, `pc_bonus` [equip][R-equip] [base range/passives][R-jobstats] [R-rates] [bonus][R-bonus] | `SizeFixDatabase::getDefaultLocation/parseBodyNode`, `status_calc_pc_sub`, `battle_calc_sizefix` [load][R-sizeload] [root][R-sizedb] [overlay][R-sizere] [apply][R-sizebattle] |
| Call chain | Equipped hand ranges/scripts → each at least1 → right=max(right,left) → weapon-specific range passives | Root+RE+custom DB → right/left weapon size arrays → attack component size fix |
| Input dependencies | Equipped weapon/hand/ammo context, passive skills, status/job state | Weapon family, target size, special no-size-fix state, riding/weapon exceptions |
| Formula / modifiers | Base item range plus context bonuses; Owl's Eye, Vulture's Eye, Snake Eye and other applicable skill branches in source; `SCB_RANGE` exists but battle main explicitly has no status-change range implementation | Multiply applicable weapon component by table percentage then integer `/100`; dagger100/75/50; reference no-size-fix flag can bypass |
| Arithmetic semantics | Unsigned16 field; minimum and merge order matter; range validation belongs to authoritative action path | Integer percentage applies at component stage, not entire attack total |
| Existing Athena / coverage | Definition/resolver/basic rules use range, no full hand/skill integration; **Partial** | `WeaponAttackCalculator` assumes100 and ignores existing `MobDefinition.Size`; **Missing** |
| Test evidence | Resolver/range tests cover known weapon and safe unknown; missing passives/dual hand | Existing Knife expectations omit GPoring's Medium size penalty; no size oracle |
| Future work | Snapshot equipment-derived range; preserve World authoritative range validation | Minimum prerequisite for claiming the existing dagger attack scope, not an excuse to implement all combat card/element logic |

## Modifier integration and fourth-job boundary

### RS-EQUIP — bonus-source ordering and validation

| Field | Specification |
|---|---|
| Reference function / source | `status_calc_pc_sub`, `pc_bonus`: [equipment][R-equip], [scripts][R-scripts], [bonus dispatcher][R-bonus], [remaining base pass][R-pctail]. |
| Call chain | Validate/resolve equipped slots → base item/refine/grade → equip script → ammo → combo → snapshot equip params/reset general params → cards → random options → status/bonus scripts/pet → job/stat/derived passes. Reentrant script calculation returns through guard/abort paths. |
| Input dependencies | Authoritative unique inventory instance/slot, definition, equip mask, refine/grade, cards/options, combos, map restrictions/permissions, pet, persistent script bonuses, skill tree. |
| Formula / modifier dependencies | `pc_bonus` dispatch is semantic: primary/trait `param_bonus`; Renewal bBaseAtk→eatk; ATK1/2 and range respect hand/ammo; CRI/perfect dodge scale values by10; bMaxHP/SP flat differ from rates; `bSpeedRate` chooses a best value while stacking variant accumulates; `bAspd` subtracts10×value, `bAspdRate` adds to `aspd_rate2`; HP/SP recovery rates separate. |
| Arithmetic semantics | Per-bonus caps/types, different accumulators, scripts in fixed source order; armor refine aggregates before `(sum+50)/100`. Do not round each piece first. Slot alias de-duplication prevents counting multi-slot gear twice. |
| Existing Athena / coverage | `ItemDefinition`/`EquippedWeaponResolver`/equipment mutation cover identity, ATK/range and equip positions, not complete stats or bonus execution. **Partial** identity layer; **Deferred** complete script/card/skill bonus machinery. |
| Test evidence | `CharacterEquipmentMutationServiceTests`, resolver tests cover structural validation and persistence failure; no complete modifier oracle. Removing conflicts and equipping target uses separate durable mutations; a partial persistence failure needs reconciliation before stats can safely assume one committed equipment set. |
| Future work | Immutable item-instance snapshot and typed source-stage contributions, only as each mechanic needs them; no universal plugin/strategy hierarchy without a consumer. Later `pc_bonus2/3/4/5`, autobonus, combat cardfix and script execution require their own branch audits before enablement. |

### RS-TRAIT — POW, STA, WIS, SPL, CON, CRT and their substats

| Field | Specification |
|---|---|
| Reference function / source | `status_calc_pc_sub`, `status_calc_misc`, `status_calc_pow/sta/wis/spl/con/crt`, `status_calc_patk/smatk/res/mres/hplus/crate`, `status_calc_bl_main`: [assembly][R-jobstats], [base][R-misc], [attribute helpers][R-traits], [substat helpers][R-traitbonus], [deltas][R-traitdelta]. |
| Call chain | Job/equip/allocated traits assembled beside six classic stats → misc substats → base rates/passives → temporary traits set dependent bits → substat helpers. |
| Input dependencies | Six allocated/job/equipment traits, fourth-job/passive/status data, level5 weapon refinement, existing base substats. |
| Formula | POW adds5× to BATK; SPL adds5× to MATK; CON adds2× to HIT/FLEE. Base PATK=`POW/3+CON/5`, SMATK=`SPL/3+CON/5`, RES=`STA+(STA/3)*5`, MRES=`WIS+(WIS/3)*5`, HPLUS=`CRT`, CRATE=`CRT/3`. |
| Modifier dependencies | Benedictum/Religio/Festival attribute helpers, fourth-job passive/rate contributions, ordered substat statuses. POW/CON deltas feed PATK before its helper; SMATK adds trait deltas after its helper. RES/MRES use division of the trait delta, not subtraction of two individually divided base expressions. |
| Arithmetic semantics | Base/helper substats cap0..32767; narrow fields; negative delta division toward zero; different helper/delta placement can bypass a prior cap. Traits use unsigned-return/signed-storage pattern. |
| Existing Athena / coverage | `CharacterGameplayState` and progression definitions have no six-trait authoritative state; combat assumes POW0. **Deferred**, with **Missing** prerequisite storage/data. This is not equivalent for fourth jobs simply because their job ID is present in generated progression. |
| Test evidence | Need all six trait increments, `/3` and `/5` thresholds, negative delta, caps, passive/helper interactions and source asymmetric order. No current independent fixtures. |
| Future work | Preserve stages and explicit unsupported capability in Epic 15 architecture. Fourth-job allocation, AP, complete skill/weapon systems require separate approved scope; never manufacture zero traits for a character that should have nonzero authoritative traits. |

## Detailed existing-combat discrepancy: RS-MOB

| Field | Specification |
|---|---|
| Mechanic | Renewal ordinary monster physical attack inputs and player defense consumption; audited to establish the stat-engine boundary, not the whole combat algorithm. |
| Reference function / source | `MobDatabase::parseBodyNode/loadingFinished`, `status_calc_misc`, `status_base_atk_min/max`, `status_calc_mob_`, `battle_calc_base_damage`, `battle_calc_defense_reduction`, `battle_calc_attack`: [parse][R-mobparse], [load][R-mobload], [misc][R-misc], [bounds][R-mobrange], [spawn][R-mobspawn], [dispatch][R-mobdispatch], [roll/base][R-baseattack], [defense][R-defdamage], [final outcome][R-attackend]. |
| Call chain | `Attack`→`rhw.atk`; Renewal `Attack2`→`rhw.matk` → DB loading constructs dummy BL_MOB and calls misc → computes upper bound before overwriting lower bound and adds enabled BATK → ordinary spawn copies calculated DB status → weapon attack non-PC branch → base damage → downstream defense/outcome. |
| Input dependencies | Mob definition level/STR/Attack, actor config, copied/specially adjusted runtime status, active mob statuses, target full battle defense and attack-context skills/status/target-count. |
| Formula | Ordinary bounds `lo=Attack*80/100`, `hi=Attack*120/100`, each stored `uint16`; enabled mob BATK=`clamp(STR+L,0,65535)`. Noncritical roll is `lo + (hi>lo ? rnd()%(hi-lo) : 0)`; **upper-exclusive** when unequal. Add BATK unless `BDMG_MAGIC`. Critical selects hi, then later multiplies by14/10; Maximize Power makes bounds equal. |
| Modifier dependencies | Mob special-level/master/summon rebuild paths, nondefault masks, Maximize Power/crit branch, downstream damage/status effects. On target: hard DEF from real equipment; soft DEF from staged status engine; AL_DP/Ranger Main/Research Fire & Earth versus qualifying mobs; VIT crowd penalty; ignore/pierce/simple-defense and status adjustments. |
| Arithmetic semantics | Bounds narrow to unsigned16 before roll; `rnd()%width` preserves the pinned RNG/modulo behavior (do not claim statistically exact uniform distribution from modulo). Equal or reversed bounds use lo without a roll. Damage is int64; ordinary defense `damage*(4000+hardDEF)/(4000+10*hardDEF)-softDEF`, with `hardDEF==-400` changed to-399. Sub-one final attack normalization is separate from HIT/FLEE failure. |
| Existing Athena / coverage | `MobBasicAttackCalculator` correctly distinguishes Attack2 as MATK, but **omits BATK**, uses inclusive `Random.Next(lo,hi+1)`, no uint16 bound narrowing, hard DEF0 and freshly computed effective-stat soft DEF; no accuracy. **Partial, incorrect even in its claimed ordinary unarmored scope**. |
| Test evidence | `Calculate_RealGeneratedGPoring_AgainstFreshNoviceStats_IsAuthoritativelyAMiss` locks in missing BATK. Other injected-roll tests do not test exclusive bounds or the actual RNG path. No C++ reference fixtures. |
| Future work | Oracle first, then a focused correction to base/roll/defense inputs and tests. Preserve existing damage-command idempotency; do not mix full accuracy/critical/element/skill overhaul into that correction. |

The pinned [G_PORING record][R-gp] has level1, STR6, Attack1 and **Medium** size. Static arithmetic therefore gives stored bounds 0 and1, ordinary noncritical roll0, BATK7, and pre-defense damage7. An unmodified unarmored level1/VIT9/AGI9 target has base soft DEF6, so this **isolated connected-hit defense calculation** gives1. These are deductions from source, **not executable-reference golden outputs and not a claim that every complete attack must hit for1**. The official capture's zero-damage retaliation cannot establish that BATK should be omitted: accuracy, full actor state, equipment and other downstream conditions were not captured as a complete calculation input.

The same target's Medium size invalidates the player Knife tests' stated100% dagger modifier. The root size DB sets75%, retained by the Renewal overlay. Captured player damage37 also lacks a complete controlled state; it cannot justify keeping incorrect size rules or selecting constants to fit one observed hit.

## Detailed status examples: RS-BLESS / RS-INCAGI

| Field | Blessing (RS-BLESS) | Increase AGI (RS-INCAGI) |
|---|---|---|
| Reference function / source | Skill `castendNoDamageId`, script `sc_start`, `status_change_start/post_delay/end`, `status_calc_str/int/dex/hit` [skill][R-blessskill] [script][R-scscript] [entry][R-start] [values][R-blessval] [DB][R-statusdb] | Same lifecycle, `status_calc_agi/speed/aspd` [skill][R-agiskill] [script][R-scscript] [entry][R-start] [values][R-agival] [DB][R-statusdb] |
| Call chain | Resolve target/skill or script → validate status application → cure/overlap/value initialization → install timer/entry → calc flags → dependent calculations → projections; timer end validates identity → removes entry → recalc | Same; Mado and Quagmire fail branches, Decrease AGI/Adoramus ending rules before installation |
| Input dependencies | Actor type/alive/map/resistance, skill/script level+duration/flags, curse/stone/other active statuses, current buff level/deadline, base attributes, all shared derived inputs | Same plus Mado/Quagmire/slow states and movement/ASPD inputs |
| Formula | Ordinary PC `val2=val1`, adds to STR/INT/DEX; HIT helper adds2×val1. Non-PC undead/demon sets val2=0 and follows different helper behavior; skill undead handling is separate | `val2=2+val1`, val1 contributes nonfixed ASPD, haste selection25 |
| Modifier dependencies | DB flags STR/INT/DEX/**HIT**, resulting BATK/MATK/MDEF2/MAXSP/ASPD/regen; cure Curse or Stone can end ailment and return without installing Blessing [cure][R-cure] | DB AGI/SPEED/ASPD, propagation FLEE/DEF2/DSPD; speed is explicit, not inferred from AGI |
| Arithmetic semantics | Same primary/derived stages above; no cumulative stacking of two same-type entries. Default overlap rejects weaker reapplication while returning success; old strength **and expiration remain** [overlap][R-overlap]. Equal/higher replaces timer [install][R-install] | Same overlap level policy; rejected starts must not produce a new authoritative calculation or icon representing an uninstalled effect |
| Existing Athena / coverage | `CharacterStatusEffectState.Start` unconditionally replaces, Recalculate adds level, MapClientSession sends icon plus three primary stats only. **Partial** | Same, plus movement and attack-speed bonus consumed by limited calculators. **Partial** |
| Test evidence | **Epic 15.2a (implemented, reference-verified):** `CharacterStatusEffectState.Start` now returns `Started/Replaced/Retained`; a weaker start over a stronger unexpired Blessing/Increase AGI is retained with its level and deadline, equal/higher replaces and re-arms, matching fixtures VF-07/VF-08 (`StatusRefresh_MatchesReference`). Old 10→3 overwrite tests were corrected to the pinned semantics. Existing fake-clock same-level refresh and expiry tests do not cover lower-level preservation/cure/failure or derived resync | Existing +12, expiry/movement tests omit source fail/slow-end and full derived cascade |
| Future work | Atomic transition outcome: rejected/retained/replaced/ended; same immutable before/after snapshot and exact timer identity; complete supported derived effects; wire evidence separately | Same, with mid-walk timing/public-speed update and frozen committed attack retry |

See [source install/end][R-install] [R-end] [R-endtail]. The reference start path also checks target existence, live state, disabled-on-map/Gravity/Mado/boss rules and `status_get_sc_def` resistance before application. This audit does not claim a complete port of all status-resistance/skill-casting rules; deferred statuses require their own success **and** failure branch audit. A trusted NPC-script command and a client skill request are not interchangeable authorization paths.

## Athena integration audit and evidence limits

| Component | Verified current behavior | Gap / required integration |
|---|---|---|
| `CharacterGameplayState` | Immutable allocated six stats and current/max HP/SP, job/levels/version | No coherent derived snapshot, trait or modifier provenance. Persisted maxima can be stale after non-level stat changes. |
| `CharacterGameplayStateSession` / CharServer persistence contracts | Mutation semaphore, expected-version persistence, ownership validation, update runtime only after persistence | Progression currently computes from `State` **before** acquiring the mutation gate and passes `_ => calculation.After`; a concurrent mutation can be overwritten. Compute candidate and derived outputs from state inside the authoritative transition. |
| `CharacterProgressionService` | Job VIT/INT added for its HP/SP path; level-up heals, job-only change clamps | Duplicate resource formula; no equip/status/class/rate input. Reuse central calculator while preserving progression's separate refill semantics. |
| `CharacterStatusEffectState` | Locked dictionary and six-stat projections, fake-clock support | Three independently locked before/expire/after operations are not one atomic transition; refresh/start may interleave. Narrow casts do not implement reference caps. |
| `PlayerCombatSnapshot` / `MapClientSession.TryGetCombatSnapshotAsync` | Snapshot contains live position, base level, VIT/AGI/alive/walking | No hard DEF, derived DEF2 or revision. Effective stats are read before awaiting movement gate and other gameplay fields afterwards: possible mixed revisions. |
| `RenewalBasicAttackRules` / `WeaponAttackCalculator` | Resolver-based unarmed/weapon attack and explicit limited scope | Recomputes BATK locally; misses size/refine/job/traits and broader combat stages. Accepting a character state is wider than a comment declaring a fresh Novice. |
| `MobBasicAttackCalculator` | Separate non-PC Attack versus Attack2 handling; zero-damage outcome path | RS-MOB errors, hard DEF absent, soft-DEF recalculation duplicates a different stage. |
| `AttackDelayCalculator` / movement / damage-motion calculators | Small pure helpers used by live consumers | ASPD cap and job issues, fragmented inputs/config; `MapClientSession` player-attack projections hardcode `srcSpeed:460` at the fast/feed paths instead of derived motion. |
| `EquippedWeaponResolver` / `ItemDefinition` | Authoritative right-hand instance-to-definition lookup; unsupported IDs/types are detectable | Stats pipeline needs whole authoritative equipment set and refinement. Armor DEF, weapon MATK and script modifiers absent from definitions. |
| Generated progression / compiler | Six bonus arrays, per-job caps, HP/SP and EXP tables tied to reference data | No generated job ASPD/trait stats; generated identity or table coverage is not gameplay equivalence. |
| `MapClientSession` client update paths | Exact captured primary/stat-up packet subset; post-persistence success | Buff start sends raw individual bonus, not total effective-minus-allocated; stat-up/expiry read state across awaits; derived packets largely missing. Must diff one immutable projection and serialize in verified order. |
| World combat/runtime | Grain owns monster HP/life/feed/ledger; MapServer owns character calculation and incoming HP mutation | Keep exact committed damage/sequence/retry command. Do not add a stat grain or recalculate a committed attack after a timeout. |

Primary audited Athena paths: [stat service](../../src/MapServer/World/CharacterStatService.cs), [session/persistence](../../src/MapServer/Net/ICharacterGameplayStatePersistence.cs), [CharServer persistence](../../src/CharServer/Net/MapServerSession.cs), [internal state protocol](../../src/MapServer/Net/MapCharacterGameplayStateProtocol.cs), [combat snapshot](../../src/MapServer/World/PlayerCombatSnapshot.cs), [basic rules](../../src/MapServer/Gameplay/Rules/Renewal/RenewalBasicAttackRules.cs), [weapon damage](../../src/MapServer/Gameplay/Rules/Renewal/WeaponAttackCalculator.cs), [mob damage](../../src/MapServer/Gameplay/Rules/Renewal/MobBasicAttackCalculator.cs), [attack delay](../../src/MapServer/World/AttackDelayCalculator.cs), [movement speed](../../src/MapServer/World/MovementSpeedCalculator.cs), [damage motion](../../src/MapServer/Gameplay/Rules/Renewal/PlayerDamageMotionCalculator.cs), [resolver](../../src/MapServer/World/EquippedWeaponResolver.cs), [items](../../src/MapServer/World/ItemDefinition.cs), [Map session](../../src/MapServer/Net/MapClientSession.cs).

Test paths: [progression](../../tests/MapServer.Tests/World/CharacterProgressionServiceTests.cs), [effects](../../tests/MapServer.Tests/World/CharacterStatusEffectStateTests.cs), [stat validation](../../tests/MapServer.Tests/World/CharacterStatServiceTests.cs), [stat-up](../../tests/MapServer.Tests/Net/MapClientSessionStatUpTests.cs), [expiry](../../tests/MapServer.Tests/Net/MapClientSessionStatusExpirationTests.cs), [weapon](../../tests/MapServer.Tests/Gameplay/Rules/Renewal/WeaponAttackCalculatorTests.cs), [monster](../../tests/MapServer.Tests/Gameplay/Rules/Renewal/MobBasicAttackCalculatorTests.cs), [retry](../../tests/MapServer.Tests/Net/PendingMonsterDamageAttemptTests.cs), [generated data](../../tests/MapServer.Tests/World/GeneratedCharacterDataTests.cs).

Passing these tests establishes the behavior they assert; it does not resolve the discrepancies above. No C++ harness, source-produced numeric fixture, live iRO experiment or new packet capture was executed for this audit.


## Pinned source links

All links below resolve to commit `e985006171d2eb320ee512a653f4c83aea3d81b6`. Ranges are inclusive; no upstream branch URL is used.

[R-renewal]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/config/renewal.hpp#L1-L78 "src/config/renewal.hpp:1–78 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-macro]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.hpp#L3682-L3688 "src/map/status.hpp:3682–3688 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-wrapper]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6525-L6743 "src/map/status.cpp:6525–6743 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-pcwrap]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4997-L5012 "src/map/status.cpp:4997–5012 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-pcbase]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3754-L4994 "src/map/status.cpp:3754–4994 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-main]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L5837-L6515 "src/map/status.cpp:5837–6515 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-install]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L13183-L13235 "src/map/status.cpp:13183–13235 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-endtail]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L14075-L14120 "src/map/status.cpp:14075–14120 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-reset]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3754-L3901 "src/map/status.cpp:3754–3901 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-types]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.hpp#L3361-L3416 "src/map/status.hpp:3361–3416 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-deftype]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/config/const.hpp#L40-L56 "src/config/const.hpp:40–56 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-confbattle]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/conf/battle/battle.conf#L1-L121 "conf/battle/battle.conf:1–121 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-blocktypes]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/map.hpp#L292-L305 "src/map/map.hpp:292–305 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-confplayer]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/conf/battle/player.conf#L45-L135 "conf/battle/player.conf:45–135 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-maxbase]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4353-L4405 "src/map/status.cpp:4353–4405 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-confadjust]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/battle.cpp#L8962-L8980 "src/map/battle.cpp:8962–8980 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-aspdcap]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.cpp#L15251-L15259 "src/map/pc.cpp:15251–15259 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-defdamage]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/battle.cpp#L4720-L4884 "src/map/battle.cpp:4720–4884 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-regenrate]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L5377-L5508 "src/map/status.cpp:5377–5508 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-regentick]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L15444-L15660 "src/map/status.cpp:15444–15660 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-jobparse]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.cpp#L13810-L14335 "src/map/pc.cpp:13810–14335 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-sizeload]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L491-L581 "src/map/status.cpp:491–581 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-sizedb]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/db/size_fix.yml#L30-L88 "db/size_fix.yml:30–88 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-sizere]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/db/re/size_fix.yml#L30-L39 "db/re/size_fix.yml:30–39 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-jobstats]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4216-L4348 "src/map/status.cpp:4216–4348 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-edges]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L5837-L5974 "src/map/status.cpp:5837–5974 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-str]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6752-L6818 "src/map/status.cpp:6752–6818 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-agi]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6827-L6891 "src/map/status.cpp:6827–6891 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-vit]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6900-L6952 "src/map/status.cpp:6900–6952 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-int]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6961-L7030 "src/map/status.cpp:6961–7030 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-dex]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7039-L7105 "src/map/status.cpp:7039–7105 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-luk]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7114-L7166 "src/map/status.cpp:7114–7166 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-statup]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.cpp#L8798-L8921 "src/map/pc.cpp:8798–8921 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-baseatk]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L2424-L2496 "src/map/status.cpp:2424–2496 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-misc]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L2600-L2765 "src/map/status.cpp:2600–2765 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-batk]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7295-L7330 "src/map/status.cpp:7295–7330 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-atkdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L5975-L6001 "src/map/status.cpp:5975–6001 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-bonus]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.cpp#L3725-L4035 "src/map/pc.cpp:3725–4035 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-equip]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3902-L4042 "src/map/status.cpp:3902–4042 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-weaponsum]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L2505-L2507 "src/map/status.cpp:2505–2507 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-watk]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7339-L7422 "src/map/status.cpp:7339–7422 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-basematk]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L2559-L2592 "src/map/status.cpp:2559–2592 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-matkdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6283-L6372 "src/map/status.cpp:6283–6372 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-matkbonus]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7431-L7495 "src/map/status.cpp:7431–7495 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-display]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.hpp#L1237-L1253 "src/map/pc.hpp:1237–1253 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-scripts]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4044-L4214 "src/map/status.cpp:4044–4214 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-rates]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4407-L4610 "src/map/status.cpp:4407–4610 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-def]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7748-L7848 "src/map/status.cpp:7748–7848 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-defdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6004-L6083 "src/map/status.cpp:6004–6083 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-mdef]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7923-L7972 "src/map/status.cpp:7923–7972 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-hit]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7544-L7603 "src/map/status.cpp:7544–7603 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-flee]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7612-L7715 "src/map/status.cpp:7612–7715 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-critdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6138-L6162 "src/map/status.cpp:6138–6162 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-cri]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7504-L7535 "src/map/status.cpp:7504–7535 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-aspdbase]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L2355-L2416 "src/map/status.cpp:2355–2416 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-aspd]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L8237-L8364 "src/map/status.cpp:8237–8364 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-aspdfix]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L8376-L8395 "src/map/status.cpp:8376–8395 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-timingdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6375-L6449 "src/map/status.cpp:6375–6449 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-timingbase]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4611-L4646 "src/map/status.cpp:4611–4646 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-speed]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L8018-L8225 "src/map/status.cpp:8018–8225 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-speeddelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6086-L6136 "src/map/status.cpp:6086–6136 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-maxhp]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3511-L3553 "src/map/status.cpp:3511–3553 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-hpbonus]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3138-L3289 "src/map/status.cpp:3138–3289 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-maxsp]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3562-L3604 "src/map/status.cpp:3562–3604 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-spbonus]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3298-L3418 "src/map/status.cpp:3298–3418 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-hpjob]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.cpp#L13710-L13783 "src/map/pc.cpp:13710–13783 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-maxdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6198-L6239 "src/map/status.cpp:6198–6239 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-regen]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L5264-L5369 "src/map/status.cpp:5264–5369 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-sizebattle]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/battle.cpp#L2427-L2440 "src/map/battle.cpp:2427–2440 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-pctail]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4650-L4994 "src/map/status.cpp:4650–4994 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-traits]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7175-L7286 "src/map/status.cpp:7175–7286 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-traitbonus]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L8548-L8703 "src/map/status.cpp:8548–8703 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-traitdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6450-L6515 "src/map/status.cpp:6450–6515 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-mobparse]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/mob.cpp#L5098-L5118 "src/map/mob.cpp:5098–5118 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-mobload]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/mob.cpp#L5537-L5595 "src/map/mob.cpp:5537–5595 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-mobrange]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L2522-L2554 "src/map/status.cpp:2522–2554 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-mobspawn]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L2775-L2821 "src/map/status.cpp:2775–2821 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-mobdispatch]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/battle.cpp#L4135-L4147 "src/map/battle.cpp:4135–4147 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-baseattack]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/battle.cpp#L2518-L2636 "src/map/battle.cpp:2518–2636 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-attackend]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/battle.cpp#L6753-L6796 "src/map/battle.cpp:6753–6796 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-gp]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/db/re/mob_db.yml#L57447-L57474 "db/re/mob_db.yml:57447–57474 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-blessskill]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/skills/acolyte/blessing.cpp#L1-L29 "src/map/skills/acolyte/blessing.cpp:1–29 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-scscript]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/script.cpp#L12401-L12465 "src/map/script.cpp:12401–12465 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-start]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L10152-L10297 "src/map/status.cpp:10152–10297 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-blessval]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L11567-L11572 "src/map/status.cpp:11567–11572 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-statusdb]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/db/re/status.yml#L445-L481 "db/re/status.yml:445–481 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-agiskill]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/skills/acolyte/incagi.cpp#L1-L29 "src/map/skills/acolyte/incagi.cpp:1–29 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-agival]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L10846-L10854 "src/map/status.cpp:10846–10854 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-cure]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L10705-L10718 "src/map/status.cpp:10705–10718 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-overlap]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L10748-L10806 "src/map/status.cpp:10748–10806 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-end]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L13430-L13520 "src/map/status.cpp:13430–13520 at e985006171d2eb320ee512a653f4c83aea3d81b6"
