# Feature 16 — Complete Item & Equipment Effects (branch `feature/epic-16-item-effects`, one PR)

Phase 3 / Epic 3, Feature 16. Reference: pinned rAthena Renewal `e985006171d2eb320ee512a653f4c83aea3d81b6` (`legacy/rathena`). Baseline: `main` @ `33c0186` (Feature 15 merged). One branch, one PR, left open, never merged without explicit approval. Some older docs call these "Epic N"; Feature N and Epic N are the same numbering.

## Ownership (confirmed)

| Concern | Owner |
|---|---|
| Item data and metadata, equip eligibility/slots, equip/unequip effects, bonus accumulation, refine and enchant grades, cards/combos/set bonuses, random options/enchants, inventory/storage/cart/ground-item rules, identify/break/repair, bound/rental/expiry, item use/containers, item-transfer primitives, item-triggered-effect registration and dispatch contract | **Feature 16** |
| Combat formulas that consume accumulated bonuses (cardfix, elements, accuracy/crit rolls, ranged/dual-wield formula branches) | Feature 17 |
| Skill execution (autospell/itemskill targets, Heal, repair/break *as skills*) | Feature 18 |
| General-purpose script execution (`goto/callsub/callfunc`, variables, loops, NPC refine/repair scripts) | Feature 20 |
| Trade sessions, mail, vending, auction, buying store, party loot sharing | later social/economy features (F16 supplies the atomic, idempotent transfer primitives) |

Cards split: F16 owns cards as items (slots, compound, combos, bonus accumulation); F17 owns `battle_calc_cardfix` and every formula that reads the accumulated bonuses.

## Architecture decisions (final)
1. **Scripts.** Item/combo/randopt scripts go through the existing `RathenaLexer/Parser` → `Semantics` → `ScriptLowering` pipeline, extended with an item/player context and a bonus-command catalog. No second language or runtime. The lowered IR is serialized into the item data pack; the original script source is always kept with a support classification (`Lowered` or `Deferred(F18|F20, construct)`). A MapServer evaluator over the typed IR runs through an `IItemEffectHost` and produces `EquipmentBonuses`. F20 later extends the lowerer/host, not the item-effect data model.
2. **Regions.** New rule surfaces (`IEquipmentRules`, `IItemBonusRules`, `IInventoryRules`) are implemented by separate concrete `Iro*`/`Kro*` classes, registered only by the matching branch of `GameplayRulesFactory`. No region flags in gameplay code, no shared concrete algorithms, parity/architecture tests extended. Shared: generated data, DTOs/protocols, packet serializers.
3. **Item data.** A versioned, deterministic binary data pack (pinned commit hash and content hash in the header) loaded once into lookup tables, plus small generated C# for enums/constants. Committed when its measured size is reasonable (S1 spike result below); CI verifies deterministic regeneration and the hash. No hand edits.
4. **Persistence.** CharServer owns durable inventory, equipment, storage, cart. All mutations carry an operation id and are idempotent; multi-row mutations are atomic; ground-item claim is exactly-once across concurrent pickup, lost acks, retries and crashes.
5. **Protocol.** Server-authoritative behavior is independent of packet serialization. Only iRO packets backed by captures are projected; everything else is listed under "Outstanding captures".

## Completion definition
Every requirement below is *Implemented* (rules + tests, plus a reference fixture where pinned rAthena has the behavior), *Owned elsewhere* (F17/F18/F20 with the exact construct, plus a hook/test that proves the contract), or *Protocol-pending-capture* (server logic done and tested; only wire projection waits). `Unsupported` markers never count as implementation. No silently ignored bonus, item effect or persistence column (automated catalog/column coverage tests). Existing tests may change only when a contract legitimately changes, never weakened.

## Data-driven acceptance requirement (binding, S3 onward)
pAthena must support fully data-driven item conversion and execution: add/update an item in the rAthena source data, run the existing WorldDataImporter pipeline, load the regenerated data pack, and use the item with every effect it declares supporting — with zero additional handwritten C# for that specific item, no manual registration, and no item-specific patching. WorldDataImporter (`ItemDatabaseLoader` → `ItemPackBuilder` → the pack format → `ItemCatalog`/`ItemMetadata`) stays the single source-to-runtime conversion pipeline for all item metadata and item scripts (`Script`/`EquipScript`/`UnEquipScript`) - no second item conversion pipeline. The runtime interprets the generated data and typed script IR (`RathenaLexer/Parser` → `Semantics` → `ScriptLowering` → `IrNode`/`PackScript`) through general-purpose, reusable effect handlers: `bonus bStr,5;`, `bonus bMaxHP,100;`, `itemheal`, `sc_start`, and every other script command work identically for any item that declares them, implemented once in the shared command-handler/`IItemEffectHost` layer, never by switching on `ItemId`, `AegisName`, or a generated item-name constant in production code. New reusable commands are implemented once in that general layer and become available to every item using them. Constructs explicitly owned by F17/F18/F20 keep their typed integration contract with execution marked deferred until that feature lands - never faked. Unsupported constructs get an actionable diagnostic (item, source, script command, responsible feature) - never silent effect loss. A script stored in the pack or marked `Lowered` is not by itself proof its effects execute; track parsed/lowered/executable/deferred/unsupported as distinct states per the coverage audit below. Tests may reference concrete item IDs as fixtures; production code may never branch on item identity. Acceptance: a test-only rAthena item fixture with previously-unseen item IDs, built only from already-supported metadata/bonus/script combinations, run through the real WorldDataImporter and loaded in MapServer, must work with no production code changes; a new item using an unknown script command must produce a clear unsupported diagnostic, not silent effect loss.

## Stages (status is updated in the coverage matrix as each lands)
S0 baseline/docs · S1 item data pipeline and data pack · S2 persistence/idempotency/storage/ground-claim · S3 inventory rules, consumption, ground items · S4 equipment eligibility/slots/switch · S5 bonus model and recalculation · S6 refine and grades · S7 cards/combos/random options/enchant · S8 triggered effects and client sync · S9 completion audit.

## Pinned battle configuration (conf values, not code defaults)
The reference binary reads `conf/battle/*.conf`; Athena options take the conf value. Differences from code defaults that matter: `vending_max_value=1000000000`, `vending_tax=500`, `vending_tax_min=100000000`, `allow_consume_restricted_item=no`, `pet_equip_required=yes`, `rental_item_novalue=no`, `ban_hack_trade=5`. Item/drop defaults: `item_first/second/third_get_time=3000/2000/2000`, `mvp_item_*=10000/10000/2000`, `flooritem_lifetime=60000`, `first_attack_loot_bonus=30`, `item_use_interval=100`, `cashfood_use_interval=60000`, `natural_heal_weight_rate=70`, `open_box_weight_rate=90`, `major_overweight_rate=90`, `max_cart_weight=8000(x10)`, `default_bind_on_equip=4`, `item_stacking=yes`, `drop_rate_cap=9000`.

## PACKETVER note
The reference runner builds at `PACKETVER 20211103` (RE); stock iRO is `20220406`. Compile-time gates differ for a few client features (package select needs `MAIN_NUM >= 20220216`; enchant/reform need `RE_NUM >= 20211103`). Server rules follow reference behavior; client projection follows captures only.

## Outstanding iRO captures (nothing below is claimed as client-compatible until captured)
Stat resend after equip/unequip (0x00B0 family, ASPD/ATK/DEF), equip/unequip failure acks (0x0999/0x099A fail codes, FAILLEVEL), inventory items carrying cards/options/grade/expire/bind (0x0B39/0x0B09 fields), item add failure acks (0x0B41 results), weight update and weight-limit packets, item use of Healing/Usable with effect, identify list/reply, repair list/reply, refine UI, card insert/compound, enchant/reform/Laphine windows, equip switch (server mechanic implemented - see the S4 equip switch section for the exact capture request), favorite toggle, storage/cart open and transfer, trailing opaque byte of 0x0998/0x00AB.

**Stat resend after equip/unequip, specifically (classification: Protocol-pending-capture).** `DerivedStatComparer` (src/MapServer/Gameplay/Rules/DerivedStatComparer.cs) implements the reusable SERVER-SIDE half only - a pure before/after diff of `DerivedStats`, no packet construction, no SP_* var-id mapping, no wire I/O - and is deliberately NOT wired into `HandleEquipRequestAsync`/`HandleUnequipRequestAsync`. Reason: the existing capture evidence (stat-point-allocation flow) proves the `0x00B0` (ZC_PAR_CHANGE) wire LAYOUT and several SP_* var ids are real, but that capture's own var-id annotations do not agree internally with the pinned `_sp` enum (`map.hpp:504-505`) - e.g. the capture doc labels var 45 "DEF2" where the pinned enum says var 45 is `SP_DEF1`. Reusing a mapping with an unresolved internal discrepancy for a NEW, never-captured trigger (equip/unequip) risks compounding that error into a genuinely wrong wire projection - exactly what "never invent packet layouts without capture evidence" exists to prevent.

Exact captures needed to close this: equip **Knife** (1201, Weapon Level 1, no refine/cards/options - the plainest possible ATK-affecting item) while the character already wears **Cotton_Shirt** (2301, plain DEF-affecting armor, no script), and the matching **unequip** of each, each capture showing the COMPLETE server-to-client packet sequence (every packet id, length, and field value, in the exact order sent) around the already-proven `0x0998`/`0x00AB` request and its ack. These two minimal captures resolve: (1) which `DerivedStatField`s actually get a `0x00B0` resend after equip/unequip specifically (not just after stat allocation), (2) the correct SP_* var id for each (settling the capture-doc/pinned-enum mismatch above), (3) whether MaxHp/MaxSp ever resend here too (already flagged unresolved even for the allocation flow), and (4) the relative ordering against the existing proven equip ack/appearance packets this project already sends. Until these are captured, this requirement stays Protocol-pending-capture: the comparator is implemented and tested, the client projection is not.

Ground item: pickup request (0x0362) and removal ack (0x00A1) are VERIFIED (G108 capture, see ai/iro-2026-wire.md) and
implemented end-to-end. **Cross-replica visibility is implemented server side (S8):** World appends `Appeared` /
`Removed(PickedUp|Expired|ClaimAbandoned)` entries to a per-map, epoch-fenced, bounded ground-item feed
(`WorldGroundItemMapSimulation`, `IWorldPartitionGrain.PollGroundItemFeedAsync`; claim/release are not visibility changes;
a poll runs the lazy expiry sweep so silent expiry is still reported). Every MapServer replica polls it in the existing
per-map tick into a read-only `GroundItemFeedMirror` (cursor-filtered: duplicates, replays and stale snapshots cannot
resurrect a removed item; a new epoch or resync replaces the mirror by diff) and re-evaluates each local session's
`GroundItemSessionVisibility` at its current cell over pinned AREA_SIZE (14). World's exclusive claim stays the only pickup
authority; mirrors never create or remove items. Tests: `WorldGroundItemFeedTests` (real TestCluster, fake clock),
`GroundItemFeedMirrorTests`, and two real-session cross-replica tests (drop through replica A visible to a session on
replica B, racing pickup from B loses, completion removes it on B; walking into range).
**Client projection: Protocol-pending-capture.** The 0x0ADD appearance packet has 14 of 24 bytes unresolved (see the
"Evidence boundary" in ai/iro-2026-wire.md), so neither 0x0ADD nor the matching 0x00A1 for a bystander is sent yet.
Capture needed: with two stock clients A and B on the same map, A kills a Poring that drops Jellopy (909) at a known
cell, B standing within 14 cells; record B's complete server-to-client stream for the drop (every 0x0ADD byte), then A
picking it up (what B receives: 0x00A1 and anything else), plus B walking into range of an existing drop (whether 0x0ADD
or 0x009D is used for an already-lying item) and out of range (0x00A1 or nothing).

## Spike results (S0)
Recorded in the S1 commit message and this section once measured.

## S3 player item drop (implemented, server side; found missing in the S9 re-audit)

`CharacterItemDropService` implements pinned `pc_dropitem`/`pc_candrop` for a group-0 player: refuses unknown rows, bad
amounts, switch-registered rows, rentals, bound rows (no can_trade_bounded) and Trade.NoDrop items below their Override
level; equipped rows are refused too (divergence: pinned pc_delitem would unequip, the stock client never offers it).
Ordering across the two authorities never duplicates an item: the durable removal commits first (operation id = drop
id), then World drops under the same drop id (idempotent); a deterministic World refusal is compensated by granting the
same instance back under a drop-derived id; an unanswered World drop is retried and otherwise lost (the existing
ground-item bounded-loss policy). The dropped instance (refine, cards, options, grade, unique id) travels opaquely in
`WorldGroundItemContent.InstanceRow` and is the pickup template, so a +7 carded weapon comes back intact. Not modeled:
MF_NODROP (no map-flag runtime), sc.cant.drop, free-cell scatter. Wire (CZ_ITEM_THROW + delete ack): Protocol-pending-
capture - capture a stock client dropping 3 of a 5-stack of Red Potion (501) and a single Knife, full packet sequence.
Tests: `CharacterItemDropServiceTests` (12, real TestCluster).

## S4 equip switch and shared equip-position rules (implemented, server side)

`CharacterEquipmentMutationService` now owns the whole pinned equip-switch mechanic, server-authoritative and wire-free:
`AddToEquipSwitchAsync` (pc_equipitem with equipswitch=true), `RemoveFromEquipSwitchAsync` (pc_equipswitch_remove),
`SwitchAllAsync` (clif_parse_equipswitch_request -> ALL_EQSWITCH -> SkillEquipSwitch, gated by `EquipSwitchCooldown`, pinned
skill_db Cooldown 10000 ms) and `SwitchSingleAsync` (clif_parse_equipswitch_request_single). The loadout is the persisted
`InventoryItemRow.EquipSwitch` column only; the pinned `equip_switch_index[]` cache is derived from rows on every plan (as
pc_load does), so CharServer stays the single durable owner. Every operation is ONE `InventoryOperationKind.EquipSwitch` (15)
batch through the existing operation-id/revision contract: atomic, idempotent under lost acks, stale writers rejected.
Deliberate divergence (stricter than pinned): pc_equipswitch ignores a failing pc_equipitem and silently drops that row from
both sets; here any rejected step rejects the whole switch and nothing changes. Storage/cart transfer refuses a registered
row (pinned MSI_SWAP_EQUIPITEM_UNREGISTER_FIRST).

Pre-existing S4 defects found and fixed while sharing the planner (all pinned pc_equipitem/pc_equippoint behavior):
`Both_Accessory` items occupied BOTH accessory slots (EQP_ACC single-slot pick and the card-forced slot were missing); the
dual-wield EQP_ARMS branch was unreachable (pc_equippoint_sub's Assassin/Kagerou-Oboro rule now derived from
GeneratedJobEquipClass); costume and shadow EQI slots were not modeled, so two costume/shadow items could be worn on one
slot; bind-on-equip (`default_bind_on_equip: 4`) was never applied (no pinned item sets the flag; data-driven only).
Tests: `CharacterEquipSwitchTests` (30, real pinned catalog) and the SQL Server executor swap test.

Ammo (found missing in the S9 re-audit, now implemented): `AmmoItemDefinition` equips at EQP_AMMO whatever position the
client sends (pinned clif_parse_EquipItem), under pc_isequip's ammo_check_weapon rules (arrows need a bow/instrument/whip,
bullets and shells a gun incl. the RENEWAL grenade launcher, AMMO_DAGGER needs AS_VENOMKNIFE, cannonballs are refused to
unmounted Mechanics/Genetics - mounts are not modeled), and is never switch-registered. The session keeps the old fail ack
for ammo: pinned answers with 0x013C clif_arrowequip, uncaptured (Protocol-pending-capture: capture an Archer equipping
Arrow 1750 with Bow 1701 worn, and a failed equip without a bow).

Still not modeled on these paths (same as the normal equip): `sc.cant.equip`/SC_PYROCLASTIC, `canequip_tick`, the sitting
check before a full switch, the `npc_id`/`npc_item_flag` gate of the single switch, the AS_LEFT-skill dual-wield
alternative, pc_isItemClass ClassUpper tier eligibility (item_db Jobs upper/baby/third/fourth restrictions).

**Equip switch wire: Protocol-pending-capture.** Exact captures needed (stock iRO 20220406): open the equip-switch window
and register Knife (1201) into it, remove it, register Knife + Guard (2101) while wearing Cotton_Shirt only, press the full
switch, then the single-item switch on Guard - each showing every client and server packet (id, length, all bytes, order).
That settles 0x0A97/0x0A98 (add + ack, whose flag width changed at 20170502), 0x0A99/0x0A9A (remove + ack), 0x0A9B (the
login/list projection), 0x0A9C/0x0A9D (full switch + reply, and whether a 0x0A9D precedes the equip acks), 0x0ACE (single
switch), and which equip/appearance acks accompany each swap.

## S7 equipment-bonus script coverage

Measured by `EquipmentBonusCoverageTests` over the real `ItemCatalog.Current` (every Weapon/Armor/Card Script, every
`item_randomopt_db.yml` entry, every `item_combos.yml` shared Script). Categories: **NoScript** (never counted as coverage),
**Live** (resolves and at least one effect reaches today's status calculation), **AwaitingConsumer** (resolves into typed,
accumulated data a Feature 17/18 or not-yet-built consumer reads), **NoEffect**, **Deferred** (not lowered), **Unsupported**
(lowered but rejected: the equipment cannot be worn at all - `CanResolveAfter` - so this is never silent effect loss, but it
is a real gameplay gap). The old S7 floors counted NoScript rows as resolved; 4,378 of the 6,183 "resolved" items had no
script, so the honest pre-increment figure was 1,805 of 12,975 scripted items (13.9%).

| Population | Scripted | Resolved before (4e7841a) | Resolved after pc_bonus vocabulary | of which Live / AwaitingConsumer |
|---|---|---|---|---|
| Weapon | 2,384 | 152 | 752 | 317 / 435 |
| Armor | 5,508 | 869 | 2,566 | 1,074 / 1,492 |
| Card | 5,083 | 784 | 2,919 | 733 / 2,184 (+2 NoEffect) |
| Random option | 249 | 189 | 248 | 11 / 237 |
| Combo | 5,114 | 515 | 2,365 | 349 / 2,016 |

What changed (S7 vocabulary increment): `EquipmentBonusParameters` holds every one of the 150 single-value `pc_bonus` cases
(pc.cpp:3725-4488) with its pinned combine rule (add, x10, negated, max, min, set, bit-or, mask, flag, 0..100 cap), lr_flag
behavior (arrow/left-hand separate or ignored) and consumer; script constants are matched case-insensitively like the pinned
symbol table. `bonus2..bonus5` accept skill names (skill_name2id) and numeric sub-keys; script constants are valid values;
1-argument flag bonuses work. Live today: primary stats/bAllStats family, bHit, bFlee, bFlee2, bCritical, bDef, bDef2,
bMdef2 (CalculateDerivedStats). Everything else is typed `EquipmentStats.FlatBonuses`/`ExtendedBonuses`/`MultiBonuses`
data for its named owner. Also fixed (pinned status_calc_pc): passes run in EQI order (item scripts, ammo, combos, cards,
random options); the ammo script runs as LR_FLAG_ARROW (its stat bonuses were wrongly summed); card scripts see the carrying
row's refine/grade (they saw 0); forged/brewed/pet rows (CARD0_FORGE/CREATE/PET) skip card scripts.

**Evaluator increment (S7, same pipeline).** `EquipmentBonusResolver` now executes integer `.@` locals (scoped to one
script run), `if`/`else`, `?:`, comparison/logical/bit operators, `min`/`max`/`pow`, `vip_status` (0: pinned VIP_ENABLE is
off) and read-only equipment context: `getrefine()`, `getenchantgrade([EQI])`, `getequiprefinerycnt(EQI)`, `getequipid(EQI)`,
`getiteminfo(id, LOCATIONS/SLOT/VIEW/EQUIPLEVELMIN/WEAPONLEVEL/ARMORLEVEL/SUBTYPE)`. Every branch is validated before the
real run (an explore pass over all branches), so an item accepted at +0 can never stop resolving at +10. Character-dependent
reads (`BaseLevel`, `readparam`, `getskilllv`, `BaseJob`/`BaseClass`/`eaclass`, ...) are rejected by name, never read as 0.

| Population | Scripted | Resolved after vocabulary | Resolved after evaluator | Live / AwaitingConsumer / NoEffect-at-probe |
|---|---|---|---|---|
| Weapon | 2,384 | 752 | 1,805 | 441 / 1,311 / 53 |
| Armor | 5,508 | 2,566 | 4,102 | 1,448 / 2,610 / 44 |
| Card | 5,083 | 2,919 | 4,323 | 909 / 3,321 / 93 |
| Random option | 249 | 248 | 248 | 11 / 237 / 0 |
| Combo | 5,114 | 2,365 | 4,200 | 456 / 3,564 / 180 |

**Wearer context (S7).** `TryResolveEquipment` takes an optional read-only `EquipmentCharacter` (session state + learned
skills + sex): `BaseLevel`, `JobLevel`, `Class`, `Sex`, `readparam(bStr..bLuk|BaseLevel|JobLevel|Class|Sex)` (pinned
pc_readparam: the BASE stat) and `getskilllv` (pc_checkskill; unknown name = 0). The session passes its real wearer; the
equip-time support check passes one built from level/sex/job (values do not matter there: every branch is explored).
Without a wearer these scripts stay unsupported.

| Population | Scripted | Resolved (final) | % | Live / AwaitingConsumer / NoEffect-at-probe | Top remaining blockers |
|---|---|---|---|---|---|
| Weapon | 2,384 | 2,241 | 94.0% | 520 / 1,645 / 76 | F18 `skill` 54, values 51, `eaclass`/`BaseJob` 30 |
| Armor | 5,508 | 4,884 | 88.7% | 1,643 / 3,180 / 61 | F18 `skill` 306, `eaclass`/`BaseJob`/`BaseClass` 167 |
| Card | 5,083 | 4,790 | 94.2% | 955 / 3,726 / 109 | F18 `skill` 74, `eaclass`/`BaseJob`/`BaseClass` 101, trait `readparam` ~70 |
| Random option | 249 | 248 | 99.6% | 11 / 237 / 0 | one `for` loop (F20) |
| Combo | 5,114 | 4,609 | 90.1% | 487 / 3,907 / 215 | F18 `skill` 161, `eaclass`/`BaseJob`/`BaseClass` 197, values 120 |

Every remaining Unsupported row is unequippable (never a silent partial bonus). F16-owned remainders: `eaclass`/`BaseJob`/
`BaseClass` (need a generated MAPID job table), trait-stat `readparam` (4th-job traits are not modeled), `getequipweaponlv`/
`getequiparmorlv`/`getitempos`, `switch`, string locals. F18: `skill` grants. Client-only `hateffect`/`specialeffect2` are recorded as typed
`EquipmentStats.ClientEffects` (no server state; their wire projection is Protocol-pending-capture). F20: `for`, `setarray`,
persistent variables. Invalid pinned data: `WZ_METEORSTORM`.

Previously remaining blockers were dominated by control flow and locals (`.@r = getrefine();`, `if (...)`, ~5,900 ExprStmt + ~1,400
If + ~1,200 non-constant values) - the evaluator increment below - then F18 `skill` grants (~410), client-only `hateffect`
(~130), F20 general-script deferrals (~25) and one invalid pinned skill name (`WZ_METEORSTORM`).

## S9 completion audit (measured against commit 2199654, 2026-10-23)

Verified against actual source, generated data, contracts, and regression tests - not a documentation summary.
Status categories: **Complete** (implemented and verified), **Partial** (real, tested, honestly-measured subset),
**Deferred** (explicit, justified Feature 17/18/20 ownership with a tested integration contract), **Protocol-
pending-capture** (server logic done and tested; only wire projection waits on evidence), **Missing**.

| # | Requirement | Status | Evidence |
|---|---|---|---|
| 1-2 | Acceptance criteria / milestones (S0-S8) | Partial | S0-S6 Complete (see per-stage commit list below); S7 Partial (cards Complete, combos/random-opts/enchant Partial by measured %); S8 Partial (triggered-effect registration Complete, client sync mostly Protocol-pending-capture, cross-replica ground-item feed Missing) |
| 3 | Deterministic generation from pinned rAthena | Complete | `WorldDataImporter.Tests` (456 tests) includes byte-identical pack regeneration (`Pack_RegenerationIsByteIdenticalToTheCommittedPack`) and facade/report reproducibility checks; `git diff --check` clean on every regenerated artifact this session |
| 4 | Item/equipment catalog completeness | Complete | 29,357 real pinned items loaded (`ItemCatalog`), verified against the real loader (`70c4be0`); `ItemPipelineDataDrivenFixtureTests` proves zero item-specific production code |
| 5 | Script lowering/support classification/execution | Partial | Lowering itself is comprehensive (21,250 of 21,317 pack scripts fully lowered, 67 deferred with named construct+owner); EXECUTION (bonus resolution) is the honestly-measured 35.6%/75.9%/10.1% above - a real, substantial, separately-scoped gap (506 pinned SP_* params, ~14 modeled) |
| 6 | Item effects, healing, consumption, cooldowns | Complete | `ItemScriptEvaluator`/`IItemEffectHost`, delay-group cooldown gate (`5609d36`), `nouse_mapflag` gate (`f63c692`) |
| 7 | Equipment eligibility and bonus accumulation | Complete | Level/sex/broken/map/job-class eligibility (`ee032e9`, `d3195dc`); bonus accumulation (flat + bonus2/3 + autobonus registration) fully wired into `TryResolveEquipment` for both regions |
| 8 | Cards, combos, random options, enchantments | Partial | Cards Complete (bonus consumption + insertion); combos 10.1% resolved (49% activate, bonus payload resolution is the bottleneck); random options 75.9% resolved; enchant grade bonus multiplier AND upgrade action both Complete |
| 9 | Refinement, zeny, transaction atomicity | Complete | `CharacterRefineService`/`CharacterEnchantGradeService` both use the atomic zeny+inventory transaction (`CharacterInventoryService.ExecuteAsync`'s zeny-aware overload); concurrent-request double-charge bug found and fixed this session (`a05bc6a`), verified against real SQL Server |
| 10 | Inventory/storage/cart/ground-item persistence | Complete | `8b26d27` (atomic idempotent operations), `d8a650f` (storage/cart transfer + rental expiry), `9a436f6`/`06d5b78` (ground-item lifecycle) |
| 11 | Expiry, rental, bound, lifecycle | Complete | `d8a650f`; `NoCart`/`NoStorage`/`NoGuildStorage` enforcement (`6162685`) |
| 12 | Triggered effects and integration boundaries | Partial | Registration (autobonus/2/3 -> `TriggeredBonusDefinition`) Complete this session (`3dd9148`); on-attack DISPATCH explicitly Deferred to Feature 17 (`pc_exeautobonus`'s own combat-loop call site), with a typed, tested contract (`EquipmentStats.TriggeredBonusesOrEmpty`) |
| 13 | Client protocol evidence and synchronization | Protocol-pending-capture (mostly) | Ground-item pickup/removal (0x0362/0x00A1) and equip/unequip request framing (0x0998/0x00AB) are capture-Verified and implemented; equip/unequip derived-stat resend is explicitly Protocol-pending-capture (this session, `2199654`) with the exact 2 captures needed documented above; cross-replica ground-item VISIBILITY feed is Missing (investigated, scoped, not implemented - comparable in size to the existing ~1000-line monster/player feed subsystem) |
| 14 | Regional iRO/kRO correctness | Complete | Every S4-S8 change this session applied identically (structurally diffed, confirmed zero shared algorithm) to both `IroStatRules`/`KroStatRules`; architecture test suite enforces no shared concrete algorithm |
| 15 | Compatibility with existing architecture | Complete | Every new increment reused the existing pipeline (RathenaLexer/Parser -> Semantics -> ItemScriptLowerer -> shared `pack.Scripts`), no second script engine, no parallel serializer (random options/combos reuse `ItemPackFormat.WriteNode`/`ReadNode`) |
| 16 | Test coverage and CI reliability | Complete | MapServer.Tests 3506, WorldDataImporter.Tests 456, CharServer.Tests 237 (+23 SQL-Server-gated), World.Tests 180, LoginServer.Tests 210 - all green this session; one confirmed pre-existing load-sensitive flake (`PlayerAttackActionFanoutTests`), reconfirmed isolation-clean 3x, zero file overlap with any change made this session |
| 17 | Features falsely described as complete | None found | Every Partial/Deferred/Protocol-pending-capture item above is explicitly labeled as such in code comments, memory, and this document - no feature in this audit claims completeness beyond its measured/verified scope |

**Confirmed real gap found during this audit (not previously tracked as such): "equip switch"** (pinned
`clif_parse_equipswitch_add/remove/request/request_single`, clif.cpp:22275-22430+) - a prepared alternate
equipment loadout the player can swap into in one action. `EquipSwitch` exists only as a persisted data column
(`InventoryItemRow.EquipSwitch`); no mutation/mechanic uses it anywhere. Already correctly listed under
"Outstanding iRO captures" (`equip switch`) but not previously called out as a MISSING MECHANIC distinct from a
missing capture - this is genuinely unimplemented gameplay, not merely an unverified wire projection of an
otherwise-complete feature, and should be scoped as its own increment (data model, mutation primitive,
eligibility rules) independent of its own client capture gap.

**Overall Feature 16 status: substantially implemented, honestly partial where measured, not complete.** S0-S6
are Complete. S7's four named requirements all have real, tested implementation at an honestly-documented
percentage. S8 has triggered-effect registration Complete and most client-sync work correctly Protocol-pending-
capture per explicit user direction, with one genuinely Missing subsystem (ground-item visibility feed) and one
newly-identified Missing mechanic (equip switch). No defect found during this audit was left unfixed; every
Partial/Deferred/Missing classification above is backed by code, tests, or documented evidence, never asserted
from memory alone.
