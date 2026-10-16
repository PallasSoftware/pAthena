# Renewal calculation dependencies, transitions and client projection

Research baseline: Athena.NET `a3d006aa07a376d42c08187ea45cebc3c65384f4`; gameplay reference rAthena **`e985006171d2eb320ee512a653f4c83aea3d81b6`**. This document records the inspected PC Renewal paths. It is not an exhaustive specification of every monster, pet, skill, status or custom-script combination. See the [mechanic matrix](renewal-status-matrix.md) for formulas, coverage and discrepancies, and the [plan](renewal-status-implementation-plan.md) for verification and open investigations.

## SCB semantics: ordered flags, not an arbitrary dependency graph

`SCB_*` are **bit indices** in `std::bitset<SCB_MAX>`, not independent numeric bit masks. `SCB_BASE` requests a base rebuild; the `status_calc_pc` macro supplies `StatusDatabase::getSCB_ALL()`. ALL is all bits except DYE; BATTLE excludes BASE and DYE. Status DB `CalcFlags` is parsed separately, validating the named SCB constants. A status may request direct derived flags in addition to primary attributes: Blessing includes HIT, and Increase AGI includes SPEED/ASPD. [Enum][R-flags], [sets][R-flagsets], [macro][R-macro], [DB parser][R-flagparse], [buff records][R-statusdb].

`status_calc_bl_` copies the old **battle** state before rebuilding. A PC with pending delayed damage sets `hold_recalc` and returns unless `SCO_FORCE` is supplied. The delayed-damage callback forces a full recalculation when the counter drains. The flag set is passed **by value** to `status_calc_bl_main`, which sets dependent bits as it proceeds in source order. It is not an iterative topological solver. A newly set bit is useful because its consumer occurs later in this function. Regeneration amount recomputation is an explicit final condition, not a recursively expanded REGEN bit. [Wrapper][R-wrapper], [delayed damage][R-delayed], [main][R-main].

Full rebuilds and status-only overlays are different operations. The former reconstruct accumulators and base status and copy base into battle; the latter modify selected battle fields from existing base values, sometimes adding rounded deltas. Replacing both with an unconditional final-effective-stat formula is unsafe for soft defenses, rates, traits and damage-motion history.

## Base rebuild ordering

| Order | Source operation | Dependencies established / why order matters |
|---|---|---|
| 1 | Skill tree; initial resources on SCO_FIRST; reset accumulators and base fields [reset][R-reset] | Preserve authoritative current HP/SP/AP where the source does; recursion guard and scripts are real control flow. |
| 2 | Unique equipped items, DEF, weapon stats, refine/grade, equip scripts [equipment][R-equip] | Resolve real inventory slots; do not double-count an item occupying multiple slots. Weapon hand and no-equip-map checks affect scripts. |
| 3 | Ammo, combos; snapshot `param_equip` and clear general parameters; armor refine rounding [scripts][R-scripts] | Equipment VIT/INT later grant additional fixed HP/SP. Cards must not be folded into this bucket. |
| 4 | Cards, random options, status scripts, bonus scripts, pet [scripts][R-scripts] | Ordered script contributions can affect multiple later outputs and trigger reentrant recalculation. |
| 5 | Minimum/merged hand range, size arrays, job bonuses, primary passives, allocated+bonus attributes [assembly][R-jobstats] | Job bonus lookup uses job/class/sex and level. Super Novice conditions and skills precede derived formulas. |
| 6 | Weapon summaries; max HP/SP/AP and respawn values [summaries][R-weaponsum] [resources][R-maxbase] | Resource helpers need equip-only contributions, total attributes, class/rank and bonus groups. |
| 7 | `status_calc_misc` [misc][R-misc] | Constructs baseline HIT/FLEE/DEF2/MDEF2/CRI/PD/BATK/MATK/traits and regeneration. |
| 8 | Base rates then derived passives; hard-defense rates [rates][R-rates] | Rates and passives cannot simply be moved into the later status helper. |
| 9 | Base ASPD/dmotion, weight/cart, remaining bonus/passive/status work [timing][R-timingbase] [tail][R-pctail] | Includes state later consumed by overlay helpers, including Concentrate exclusions. |
| 10 | Copy rebuilt base to battle, preserving current resources; overlay all requested battle flags [tail][R-pctail] [main][R-main] | Initial/full calculation differs from a partial recalculation using previous battle fields. |

This ordering is the architecture constraint; it does not require reproducing rAthena's mutable C++ object layout or script engine in C#.

## Battle-pass order and exact propagated flags

For a PC in Renewal, attributes are processed **STR → AGI → VIT → INT → DEX → LUK → POW → STA → WIS → SPL → CON → CRT**. These set the following flags unconditionally when their own flag is present (not only when the attribute's numeric value changes). Non-PC extensions are called out where relevant. [Propagation][R-edges].

| Changed/requested attribute | Flags set by the source | Indirect effects after those flags run | Mechanic IDs |
|---|---|---|---|
| STR | BATK; WATK only for homunculus | PC displayed status ATK and attack inputs; no PC WATK propagation solely from STR (attack-time weapon stat bonus still consumes STR/DEX) | RS-STR, RS-BATK, RS-WATK |
| AGI | FLEE, DEF2; ASPD and DSPD for PC/homunculus | Evasion, soft DEF, attack motion/delay, damage motion; no automatic SPEED | RS-AGI, RS-FLEE, RS-DEF2, RS-ASPD, RS-DMOTION |
| VIT | DEF2, MDEF2; MAXHP for PC/homunculus/mercenary/elemental; hard DEF only homunculus | MaxHP can clamp current HP; VIT/MAXHP cause regen **amount** recalculation | RS-VIT, RS-DEF2, RS-MDEF2, RS-HP, RS-REGEN |
| INT | MATK, MDEF2; MAXSP for the same resource-bearing actor types; hard MDEF only homunculus | MaxSP can clamp current SP; INT/MAXSP cause regen amounts | RS-INT, RS-MATK, RS-MDEF2, RS-SP, RS-REGEN |
| DEX | BATK, HIT, MATK, MDEF2; ASPD PC/homunculus; WATK homunculus only | Attack input and timing changes; no PC range propagation solely from DEX | RS-DEX, RS-BATK, RS-HIT, RS-MATK, RS-MDEF2, RS-ASPD |
| LUK | BATK, CRI, FLEE2, MATK, HIT, FLEE | Multiple separately divided `/3`, `/5`, and tenths-percent effects | RS-LUK, RS-BATK, RS-CRI, RS-PD, RS-MATK, RS-HIT, RS-FLEE |
| POW | BATK, PATK | Physical base and trait attack | RS-TRAIT, RS-BATK |
| STA | RES | Physical resistance trait | RS-TRAIT |
| WIS | MRES | Magic resistance trait | RS-TRAIT |
| SPL | MATK, SMATK | Magic base and trait attack | RS-TRAIT, RS-MATK |
| CON | HIT, FLEE, PATK, SMATK | Accuracy/evasion plus two different trait-delta helper placements | RS-TRAIT, RS-HIT, RS-FLEE |
| CRT | HPLUS, CRATE | Healing and critical-damage trait outputs | RS-TRAIT |

After attributes, main processes BATK → WATK → HIT → FLEE → DEF → DEF2 → MDEF → MDEF2 → SPEED → CRI → FLEE2 → attack element → defense element → mode → MAXHP → MAXSP → MATK → ASPD → DSPD → trait substats → MAXAP → regen amounts/rates. SIZE/RACE/RANGE have explicit “no status changes alter these yet” comments in this pin; presence of those flag names does not prove an implemented overlay. Actor-specific branches remain inside the ordered stages. [Entire ordered pass][R-main].

Important stage behavior:

- BATK retains the base contribution not attributable to its formula: `base.batk - status_base_atk(base)`, added to the effective formula before the temporary BATK helper. A nonzero-base guard can skip this block. [ATK delta][R-atkdelta].
- HIT/FLEE use differences of individually divided LUK terms; DEF2/MDEF2 use a **single truncation of floating-point attribute deltas** added to an already constructed/rated base value. Do not replace these with a new full effective formula. [Defense/accuracy delta][R-defdelta].
- CRI and perfect dodge only update when the corresponding base field is nonzero. Katar doubles CRI after the status helper's clamp. [Critical/dodge][R-critdelta].
- HP/SP recompute from effective VIT/INT, apply configuration/caps and clamp current values down. Increasing a maximum is not by itself a heal. Current-resource synchronization can occur inside these blocks as well as the outer old/new comparison. [Resources][R-maxdelta].
- DSPD's PC assignment is conditional on base AGI differing from effective AGI, even when the flag is set. Whether a last-buff expiration leaves prior dmotion is a specific reference-harness question; retain this history dependency until tested. [Timing][R-timingdelta].
- PATK applies its helper after trait deltas; SMATK adds deltas after its helper. RES/MRES divide the delta, rather than subtracting divided totals. [Traits][R-traitdelta].
- VIT/MAXHP/INT/MAXSP flags invoke `status_calc_regen`; only SCB_REGEN invokes `status_calc_regen_rate`. A temporary INT change does not automatically mean rates need rebuilding. [Tail][R-traitdelta], [amount][R-regen], [rate][R-regenrate].
- SPEED sets the unit's “change walk target” marker before other work, including when the last effect ends. Existing movement is affected, not just the next walk request. [Main entry][R-edges], [speed stage][R-speeddelta].

## State-change / recalculation matrix

| Authoritative transition | Pinned origin / call path | Required inputs and propagation | Current Athena gap / implementation obligation |
|---|---|---|---|
| Allocate any of six stats | `pc_statusup → status_calc_pc(ALL)` [allocation][R-statup] | Validate stat, amount, affordable count, cap, cost; update allocated stat+points; rebuild base and battle, derived values, resource clamp, cost indicator/points then ack | Existing persistence is sound in the single-step path, but only allocated/points and primary packet change. Rebuild dependent state in the same committed transition. |
| Base Level change | `pc_checkbaselevelup → status_calc_pc(SCO_FORCE)` [level handlers][R-level] | New level changes job tables/resources, BATK/MATK/HIT/FLEE/defenses/CRI and conditional passives; all bits via rebuild | Progression computes only max resources. Keep level-up refill separate from plain recalc; calculate from current state under the mutation gate. |
| Job Level change | `pc_checkjoblevelup → status_calc_pc(SCO_FORCE)` [level handlers][R-level] | New cumulative job bonuses affect all associated derived values; max-job-dependent skills can affect ASPD; Super Novice condition can change | Generated job bonuses must reach combat and client stats, not only HP/SP. |
| Job/class/sex/rank/death-dependent state | Base pass and job lookup [assembly][R-jobstats] [max helpers][R-maxhp] [R-maxsp] | Different job stats/ASPD/class caps/passive branches/upper/fourth/ranker factors and Super Novice no-death condition | No blanket “all generated jobs supported” claim. Explicit scope and prerequisite state required. Exact trigger inventory outside the inspected common paths remains follow-up. |
| Equip / unequip / refine / card / option / combo state | Equip/unequip invoke `status_calc_pc`; base pass executes item-dependent sources [equip trigger][R-equiptrigger] [unequip trigger][R-unequiptrigger] [equipment][R-equip] [scripts][R-scripts] | Rebuild all because scripts may affect arbitrary stats; immutable resolved inventory set, per-hand types, scripts and restrictions | Resolver currently supplies just right-hand weapon definition. Do not apply a stat snapshot based on a partially failed durable equipment transition. |
| Passive skill learn/change | `pc_skillup`, passive branch calls `status_calc_pc` [skill handler][R-skillup] | Validated learned skill state feeds primary/passive/derived/range/resource/timing stages | Reuse skill persistence; new learned skill must invalidate outputs only through a defined authoritative transition. Full skill-effect implementation is deferred. |
| Status activation | `status_change_start → status_change_start_post_delay → status_calc_bl_(CalcFlags)` [entry][R-start] [install][R-install] | Validate target/alive/map/failed conditions/resistance and status-specific cures; install actual values/timer before calculating | Current generic Start accepts/replaces more broadly. Distinguish installed, retained, cured and failed results. |
| Status refresh | Overlap branch, then timer replacement when permitted [overlap][R-overlap] [install][R-install] | Weaker default refresh keeps old level and deadline; equal/higher replaces entry/timer; recalc only for actual source path | Existing test expects stronger Blessing10 replaced by3. Replace that expectation only with reference-backed behavior in a future PR. |
| Status expiration / removal | `status_change_end`, timer identity validation and removal, then flags [end][R-end] [tail][R-endtail] | No stale timer may remove a refreshed entry. Recompute relevant outputs after removal, including SP clamp and regen | Existing before/expire/after calls are individually locked, not one transaction; current output updates only four primary fields. |
| Script bonus / pet / persistent effect change | Scripts and tail during base rebuild [scripts][R-scripts] [tail][R-pctail] | May affect many accumulators and calculation recursion | Defer complete scripts/pets; unsupported effects must be diagnosable, not ignored while reporting full support. |
| Map, riding/Mado, cart/weight, sitting/walking/casting state | FLEE/speed/ASPD/regen helpers [flee][R-flee] [speed][R-speed] [aspd][R-aspd] [regen rate][R-regenrate] [timer][R-regentick] | Context changes can affect outputs without a primary-stat change | Full trigger inventory needs follow-up; define supported context explicitly. Calc functions may not fetch distributed state themselves. |
| Delayed-damage drain | `battle_delay_damage_sub` releases hold and forces full calc [delayed callback][R-delayed] | Pending calculations cannot change a previously committed logical attack | Preserve Athena's explicit immutable pending command rather than reproduce rAthena's global mutable timer design. Exact temporal-equivalence questions remain for oracle tests. |

The matrix covers the inspected dependency foundation, not every `status_calc_pc` caller in the repository. Later features must trace their own mutation/failure trigger and add a regression fixture before enablement.

## End-to-end supported-status examples

**Blessing activation** starts with authoritative target/level/duration and flags. The script path and skill path are distinct: skill undead handling is not a generic stat bonus; status application has alive/map/resistance checks; Blessing can cure Curse or Stone and return without installing the buff. For an ordinary accepted PC application, `val2=val1`, DB flags STR/INT/DEX/HIT propagate to BATK, MATK, MDEF2, MAXSP and ASPD, with INT/MAXSP causing regen amounts. HIT also receives `2*val1` independently of DEX. The full snapshot must therefore include more than the three primary increases. [Script][R-scscript], [skill][R-blessskill], [entry][R-start], [cure][R-cure], [values][R-blessval], [HIT][R-hit], [flags][R-statusdb].

**Increase AGI activation** validates Mado/Quagmire restrictions and incompatible slow removal. Accepted PC value is `val2=2+val1`. DB requests AGI/SPEED/ASPD. AGI additionally requests FLEE/DEF2/DSPD; ASPD receives both changed AGI and nonfixed `val1`, while SPEED selects the25% haste group. Recomputing only the primary AGI misses both explicit and indirect effects. [DB][R-statusdb], [values][R-agival], [AGI][R-agi], [ASPD][R-aspd], [speed][R-speed].

**Refresh/expiration** must inspect the active entry under the same transition boundary. Weaker default reapplication is a successful no-replacement path; equal/higher updates timer and values. On expiration, verify the current timer/entry identity, remove it, then compute before/after outputs from one coherent allocated/job/equipment/skill/context snapshot. Blessing expiry may reduce MaxSP and clamp current SP; Increase AGI expiry changes speed and attack timing for future actions. An already committed attack keeps its chosen inputs/result/cadence. [Overlap][R-overlap], [install][R-install], [end][R-end], [resources][R-maxdelta].

Simultaneous effects must not double-count job/equipment contributions or calculate one effect from a partly updated snapshot. “One coherent transition” is the proposed Athena design; the exact reference ordering of sequential same-tick status expirations needs a transition fixture, particularly where source delta calculations retain history.

## iRO synchronization: evidence, fields and ordering

Protocol authority is [ai/iro-2026-wire.md](../../ai/iro-2026-wire.md), especially “Verified stock-iRO base-stat allocation” and the Captain buff burst. This audit reviewed the recorded sanitized evidence and serializers, not the original raw captures or a fresh live client session. Existing document descriptions include stale implementation statements; current code, not historical prose saying no combat math exists, determines Athena coverage.

| Calculated/client value | Existing evidence | Required next action |
|---|---|---|
| Allocated six attributes and total plus | `0x0141`,14 bytes: status ID uint32 at2, base int32 at6, plus int32 at10. All six stat-up IDs13..18 verified; captured buff plus values STR/INT/DEX10, AGI12 | Plus must be **effective minus allocated**, including job/equipment/statuses, not raw just-started buff amount. Use existing serializer. |
| Stat points / HP/SP/maxima | `0x00B0`,8 bytes, parameter uint16 at2, value32 at4; points9, HP5/MaxHP6/SP7/MaxSP8 used by captured flows and fixtures | Serialize changed authoritative values from the same transition; establish signed/value semantics per parameter. |
| HIT/FLEE/CRI/PD/ASPD/speed, ATK/DEF/MDEF/MATK displays | Captures show additional `0x00B0` derived cascades; initial-state evidence also exists in [map workstream](../../ai/map-server.md). The stat-up document does not enumerate complete per-stat bursts/values | Re-extract sanitized numeric IDs/values/order for all six stat changes and controlled buff/equipment transitions. Do not invent an exact per-stat sequence from rAthena. |
| Attack range | Existing initial-state workstream evidence records `0x013A/4`; equipment/combat range paths exist | Verify change-time sequencing and values; do not treat one initial packet as proof of all dynamic behavior. |
| Active buff icon / visual | `0x0983/29`, EFST10/12, captured level10/timer240000; `0x09CB/17` visuals | Preserve capture's real val1, even though pinned status DB would send1. The cause of this difference is **not proven**; an operator-config explanation is a hypothesis. |
| Expired icon | `0x0196/9` end packet is implemented and reference-derived; Captain capture has no actual240s expiry | Obtain an official expiry/refresh capture. Do not label this path independently capture-proven; the wire document contains conflicting wording about that status. |
| Attack/movement animations and public speed | Existing action/public-presence serializers and feed tests | Feed calculated motion/delay/speed with existing units and authoritative event snapshot. Hardcoded player attack `srcSpeed:460` is a consumer discrepancy, not a new packet format. |
| Fourth-job fields | Initial parameter placeholders/IDs do not prove full trait/AP gameplay or update behavior | Deferred controlled captures alongside trait support. Never serialize invented zero as a complete trait calculation. |

Numeric parameter labels in the Captain cascade table need targeted re-extraction. For example, it labels45 “DEF2” and50 “FLEE2”, while the pinned `_sp` enum maps45 to SP_DEF1 and50 to SP_FLEE1; other labels are shifted too. The table's raw IDs remain recorded evidence; these names are interpretations, not proven client semantics. Do not fix the capture by assuming the pinned names are automatically iRO-compatible. [Reference IDs][R-spids].

Even under the pinned names, Renewal display is easy to swap: `SP_DEF1` presents soft DEF and `SP_DEF2` hard DEF; MDEF likewise. `SP_MATK1` presents the right component and `SP_MATK2` the left; neither is simply a battle MATK endpoint. CRI/perfect dodge display divides internal tenths by10; ASPD parameter carries amotion. The reference wrapper may emit both defense-side updates for one internal change. These are gameplay-to-display mappings to verify against iRO, not authority to add unverified packets. [Display macros][R-display], [serializer mapping][R-projection], [old/new comparison][R-wrapper].

Ordering constraints supported by current evidence:

1. Successful stat-up sends primary/points and additional derived updates **before the final `0x00BC` acknowledgement**. The capture proves six2→3 cases, each amount1. Persist successfully before any success projection. The exact ordering of all other stat-specific updates is not fully documented.
2. The recorded Captain burst places AGI/derived updates before the Increase AGI `0x0983`, and Blessing-related derived/primary updates before its icon, followed by the skill visual. Current `StartStatusAsync` sends icon before primary fields. Preserve the recorded ordering as a discrepancy to address with sanitized regression fixtures; do not copy rAthena activation ordering over the capture.
3. Reference expiration sends icon-off before recalculated stats. This is the current implementation's fallback behavior, **unverified on official iRO**. Keep that evidence boundary visible.
4. Multi-amount stat-up, rejected stat-up packets, values above255 and complete derived cascades need captures. Pinned `pc_statusup` calls failure acknowledgement; Athena's silent rejection cannot honestly be called matching reference behavior. [Reference allocation][R-statup].

### Prevent stale projections

Proposed flow: validate authoritative transition → capture all calculation inputs at a defined revision/time → calculate candidate base/battle/display snapshots → persist durable candidate when needed → publish runtime state and immutable transition result → enqueue ordered client/public projection. A transient status-only change has no gratuitous persistence of temporary stats. Any required durable current-resource adjustment needs an explicit transaction/failure policy (approval item in the plan).

Diff the projected **values**, including both sides of DEF/MDEF and total primary plus, rather than merely which requested primary stat changed. Preserve necessary coupled updates and captured ordering even if two packets describe values with a common source. Serialize the already-created projection; do not re-read `gameplayState.State` between awaited writes. Track a runtime calculation revision distinct from durable gameplay version so status refresh/expiration has ordering too. A serialized queue or existing session gate can prevent an older transition from being sent after a newer one; no new distributed state owner is required.

Do not merge packet serialization into the calculator. The output should express gameplay fields and transition effects; an iRO adapter chooses only verified packet formats, units and order. A full resync must restore server truth from one revision, including resource clamps, range and public speed, without deriving authoritative state from client display.

## Orleans integration and retry constraints

The current boundary is documented in [Orleans world runtime](../orleans-world-runtime.md) and implemented by [WorldPartitionGrain](../../src/WorldServer/Athena.World/WorldPartitionGrain.cs), [WorldMonsterMapSimulation](../../src/WorldServer/Athena.World/WorldMonsterMapSimulation.cs), [world contracts](../../src/WorldServer/Athena.World.Contracts/IWorldPartitionGrain.cs), and [MapClientSession](../../src/MapServer/Net/MapClientSession.cs). Some older “Phase2A/deferred” prose predates later Epic14 changes; the live code's feed/ledger authority is the constraint.

| Authority / invariant | Stat-engine fit |
|---|---|
| CharServer owns durable character state; MapServer owns the active character gameplay session and formula execution | Keep one deterministic local calculation implementation fed with authoritative immutable state. No RPC inside formulas. |
| World partition owns map runtime, presence, monster life/HP and feed sequencing | Pass derived public fields through existing commands only when needed. Do not make World independently recalculate player stats or introduce a stat grain. |
| Player-to-monster attack command has life token, presence, sequence and already-chosen damage | Freeze calculation revision, random outcome, damage and cadence when creating the logical attack. Retry the same command. A buff expiring or equipment changing during timeout must not reroll or recalculate that attack. |
| Damage ledger distinguishes exact replay, conflicting same sequence and stale sequence | Preserve command identity and existing duplicate semantics. New snapshot metadata must not accidentally turn an exact retry into a different command. |
| Action feed precedes health/death in the same mutation; local fast path deduplicates against feed | Use the same attack snapshot for animation motion and action event. Do not emit a second action because stats changed while awaiting the grain. |
| Incoming monster attacks resolve character HP locally then publish the action | Take coherent target battle stats before resolution; preserve current persistence failure/cadence behavior. Stat migration must not imply that publish-only RPC is damage authority. |
| Movement/presence have their own serialized authority | Capture gameplay/equipment/status revision consistently with the position snapshot at action validation. Avoid taking a gameplay subset before an await and another subset after it. |

The project already shares pure code through `Athena.World.Runtime` and file-linked monster source in `Athena.World.Monsters`. Reuse the established pattern only if there is an actual second formula consumer. Initially a pure calculator in the existing Renewal/World domain can serve every MapServer consumer. A new shared assembly, movement of character ownership, expanded World damage contract or persisted derived-state schema requires an explicit implementation design review. None is performed by this research.


## Pinned source links

All links below resolve to commit `e985006171d2eb320ee512a653f4c83aea3d81b6`. Ranges are inclusive; no upstream branch URL is used.

[R-flags]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.hpp#L3112-L3157 "src/map/status.hpp:3112–3157 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-flagsets]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.hpp#L3290-L3310 "src/map/status.hpp:3290–3310 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-macro]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.hpp#L3682-L3688 "src/map/status.hpp:3682–3688 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-flagparse]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L15975-L16025 "src/map/status.cpp:15975–16025 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-statusdb]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/db/re/status.yml#L445-L481 "db/re/status.yml:445–481 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-wrapper]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6525-L6743 "src/map/status.cpp:6525–6743 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-delayed]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/battle.cpp#L385-L411 "src/map/battle.cpp:385–411 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-main]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L5837-L6515 "src/map/status.cpp:5837–6515 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-reset]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3754-L3901 "src/map/status.cpp:3754–3901 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-equip]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3902-L4042 "src/map/status.cpp:3902–4042 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-scripts]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4044-L4214 "src/map/status.cpp:4044–4214 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-jobstats]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4216-L4348 "src/map/status.cpp:4216–4348 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-weaponsum]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L2505-L2507 "src/map/status.cpp:2505–2507 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-maxbase]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4353-L4405 "src/map/status.cpp:4353–4405 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-misc]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L2600-L2765 "src/map/status.cpp:2600–2765 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-rates]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4407-L4610 "src/map/status.cpp:4407–4610 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-timingbase]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4611-L4646 "src/map/status.cpp:4611–4646 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-pctail]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L4650-L4994 "src/map/status.cpp:4650–4994 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-edges]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L5837-L5974 "src/map/status.cpp:5837–5974 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-atkdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L5975-L6001 "src/map/status.cpp:5975–6001 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-defdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6004-L6083 "src/map/status.cpp:6004–6083 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-critdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6138-L6162 "src/map/status.cpp:6138–6162 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-maxdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6198-L6239 "src/map/status.cpp:6198–6239 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-timingdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6375-L6449 "src/map/status.cpp:6375–6449 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-traitdelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6450-L6515 "src/map/status.cpp:6450–6515 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-regen]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L5264-L5369 "src/map/status.cpp:5264–5369 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-regenrate]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L5377-L5508 "src/map/status.cpp:5377–5508 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-speeddelta]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6086-L6136 "src/map/status.cpp:6086–6136 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-statup]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.cpp#L8798-L8921 "src/map/pc.cpp:8798–8921 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-level]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.cpp#L8210-L8360 "src/map/pc.cpp:8210–8360 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-maxhp]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3511-L3553 "src/map/status.cpp:3511–3553 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-maxsp]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L3562-L3604 "src/map/status.cpp:3562–3604 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-equiptrigger]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.cpp#L12235-L12265 "src/map/pc.cpp:12235–12265 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-unequiptrigger]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.cpp#L12640-L12675 "src/map/pc.cpp:12640–12675 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-skillup]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.cpp#L9115-L9175 "src/map/pc.cpp:9115–9175 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-start]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L10152-L10297 "src/map/status.cpp:10152–10297 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-install]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L13183-L13235 "src/map/status.cpp:13183–13235 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-overlap]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L10748-L10806 "src/map/status.cpp:10748–10806 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-end]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L13430-L13520 "src/map/status.cpp:13430–13520 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-endtail]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L14075-L14120 "src/map/status.cpp:14075–14120 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-flee]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7612-L7715 "src/map/status.cpp:7612–7715 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-speed]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L8018-L8225 "src/map/status.cpp:8018–8225 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-aspd]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L8237-L8364 "src/map/status.cpp:8237–8364 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-regentick]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L15444-L15660 "src/map/status.cpp:15444–15660 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-scscript]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/script.cpp#L12401-L12465 "src/map/script.cpp:12401–12465 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-blessskill]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/skills/acolyte/blessing.cpp#L1-L29 "src/map/skills/acolyte/blessing.cpp:1–29 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-cure]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L10705-L10718 "src/map/status.cpp:10705–10718 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-blessval]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L11567-L11572 "src/map/status.cpp:11567–11572 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-hit]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L7544-L7603 "src/map/status.cpp:7544–7603 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-agival]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L10846-L10854 "src/map/status.cpp:10846–10854 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-agi]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/status.cpp#L6827-L6891 "src/map/status.cpp:6827–6891 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-spids]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/map.hpp#L498-L510 "src/map/map.hpp:498–510 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-display]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/pc.hpp#L1237-L1253 "src/map/pc.hpp:1237–1253 at e985006171d2eb320ee512a653f4c83aea3d81b6"
[R-projection]: https://github.com/rathena/rathena/blob/e985006171d2eb320ee512a653f4c83aea3d81b6/src/map/clif.cpp#L3637-L3815 "src/map/clif.cpp:3637–3815 at e985006171d2eb320ee512a653f4c83aea3d81b6"
