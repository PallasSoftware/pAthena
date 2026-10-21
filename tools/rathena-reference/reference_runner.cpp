// Development-only scenario driver compiled INTO the pinned rAthena map-server.
// It only sets up inputs and calls real rAthena functions; no formulas live here.
#include <cstdio>
#include <cstdlib>
#include <chrono>
#include <algorithm>
#include <cstring>
#include <fstream>
#include <string>
#include <map>
#include <vector>
#include <memory>
#include <thread>

#include "battle.hpp"
#include "clif.hpp"
#include "itemdb.hpp"
#include "log.hpp"
#include "map.hpp"
#include "mob.hpp"
#include "pc.hpp"
#include "script.hpp"
#include "status.hpp"
#include "../common/random.hpp"
#include "../common/timer.hpp"
#include "unit.hpp"

namespace rathena::server_map {

static FILE* out;
static bool first;

static void num(const char* k, long long v) { std::fprintf(out, "%s\"%s\":%lld", first ? "" : ",", k, v); first = false; }

static void dump_status(const char* name, const struct status_data& s) {
	std::fprintf(out, "\"%s\":{", name);
	first = true;
	num("str", s.str); num("agi", s.agi); num("vit", s.vit); num("int", s.int_); num("dex", s.dex); num("luk", s.luk);
	num("batk", s.batk); num("watk", s.rhw.atk); num("watk2", s.rhw.atk2);
	num("matk_min", s.matk_min); num("matk_max", s.matk_max);
	num("def", s.def); num("range", s.rhw.range); num("def2", s.def2); num("mdef", s.mdef); num("mdef2", s.mdef2);
	num("hit", s.hit); num("flee", s.flee); num("flee2", s.flee2); num("cri", s.cri);
	num("amotion", s.amotion); num("adelay", s.adelay); num("dmotion", s.dmotion); num("speed", s.speed);
	num("max_hp", s.max_hp); num("max_sp", s.max_sp); num("hp", s.hp); num("sp", s.sp);
	std::fprintf(out, "}");
}

// `job` stays last: vf02 builds specs with positional aggregate initializers (base_level, job_level, six stats).
// item_refines is parallel to items (index-matched); missing entries default to refine 0 (unrefined).
struct NoviceSpec { int base_level = 1, job_level = 1, str = 1, agi = 1, vit = 1, int_ = 1, dex = 1, luk = 1; std::vector<int> items; int job = JOB_NOVICE; std::vector<int> item_refines; };

static map_session_data* make_novice(uint32 id, const NoviceSpec& n = NoviceSpec()) {
	map_session_data* sd = new map_session_data();
	// Dancer-line jobs are female-only; the job bonus table is selected by pc_mapid2jobid(class, sex), so a male Dancer
	// would silently use the Bard row.
	pc_setnewpc(sd, id, id + 100000, 0, gettick(), n.job == JOB_DANCER ? SEX_FEMALE : SEX_MALE, 0);
	sd->m = map_mapname2mapid("prontera"); // plain map, no PvP/GvG/BG flags; player is not added to the grid
	sd->status.class_ = n.job;
	sd->class_ = pc_jobid2mapid(n.job);
	sd->status.base_level = n.base_level; sd->status.job_level = n.job_level;
	sd->status.str = n.str; sd->status.agi = n.agi; sd->status.vit = n.vit;
	sd->status.int_ = n.int_; sd->status.dex = n.dex; sd->status.luk = n.luk;
	sd->status.max_hp = 40; sd->status.hp = 40; sd->status.max_sp = 11; sd->status.sp = 11;
	memset(&sd->equip_index, -1, sizeof(sd->equip_index));
	// Real item_db entries placed in the inventory and flagged equipped at their own first location, as a loaded
	// character row would be; pc_setequipindex (the function login runs) derives equip_index, weapontype1/2 and the
	// weapon type, then status_calc_pc runs the real equipment pass.
	int slot = 0;
	for (int nameid : n.items) {
		item_data* data = itemdb_search(nameid);
		struct item& row = sd->inventory.u.items_inventory[slot];
		memset(&row, 0, sizeof(row));
		row.nameid = nameid; row.amount = 1; row.identify = 1; row.equip = data->equip;
		row.refine = slot < (int)n.item_refines.size() ? n.item_refines[slot] : 0;
		sd->inventory_data[slot] = data;
		slot++;
	}
	pc_setequipindex(sd);
	map_addiddb(sd); // as pc_authok does; status code looks the player up by id
	unit_dataset(sd);
	sd->state.active = 1;
	status_calc_pc(sd, SCO_FIRST);
	return sd;
}

// VF-01: fresh Novice, explicit stats, no equipment/skills/status.
static void vf01() {
	map_session_data* sd = make_novice(2000001);
	std::fprintf(out, "{\"id\":\"VF-01\",\"input\":{\"job\":0,\"base_level\":1,\"job_level\":1,\"str\":1,\"agi\":1,\"vit\":1,\"int\":1,\"dex\":1,\"luk\":1,\"map\":\"prontera\"},\"output\":{");
	dump_status("base_status", sd->base_status);
	std::fprintf(out, ",");
	dump_status("battle_status", sd->battle_status);
	std::fprintf(out, "}}");
}

// VF-02/VF-03: Novice job-level bonus sweep (job_stats.yml BonusStats) with explicit allocated stats; base_status
// carries the job bonus, battle_status the effective values.
static void vf02_vf03() {
	const NoviceSpec specs[] = {
		{1, 1, 5, 5, 5, 5, 5, 5}, {1, 2, 5, 5, 5, 5, 5, 5}, {1, 3, 5, 5, 5, 5, 5, 5}, {1, 4, 5, 5, 5, 5, 5, 5},
		{1, 5, 5, 5, 5, 5, 5, 5}, {1, 6, 5, 5, 5, 5, 5, 5}, {1, 7, 5, 5, 5, 5, 5, 5}, {1, 8, 5, 5, 5, 5, 5, 5},
		{1, 9, 5, 5, 5, 5, 5, 5}, {1, 10, 5, 5, 5, 5, 5, 5},
		{10, 10, 30, 20, 10, 5, 15, 8}, {99, 10, 99, 1, 77, 33, 6, 99}};
	std::fprintf(out, "{\"id\":\"VF-02\",\"input\":{\"job\":\"Novice\"},\"output\":{\"cases\":[");
	uint32 id = 2000040;
	for (const NoviceSpec& n : specs) {
		map_session_data* sd = make_novice(id++, n);
		std::fprintf(out, "%s{\"input\":{\"base_level\":%d,\"job_level\":%d,\"str\":%d,\"agi\":%d,\"vit\":%d,\"int\":%d,\"dex\":%d,\"luk\":%d},",
			id > 2000041 ? "," : "", n.base_level, n.job_level, n.str, n.agi, n.vit, n.int_, n.dex, n.luk);
		dump_status("base_status", sd->base_status);
		std::fprintf(out, ",");
		dump_status("battle_status", sd->battle_status);
		std::fprintf(out, "}");
	}
	std::fprintf(out, "]}}");
}

// VF-04: max HP/SP across base levels and VIT/INT (job_basepoints tables via status_calc_maxhp_pc/maxsp_pc).
static void vf04() {
	std::fprintf(out, "{\"id\":\"VF-04\",\"input\":{\"job\":\"Novice\",\"job_level\":1},\"output\":{\"cases\":[");
	uint32 id = 2000100;
	bool first_case = true;
	for (int level : {1, 2, 9, 10, 25, 50, 75, 99}) {
		for (int stat : {1, 7, 30, 77, 99}) {
			NoviceSpec n; n.base_level = level; n.vit = stat; n.int_ = 100 - stat > 0 ? 100 - stat : 1;
			map_session_data* sd = make_novice(id++, n);
			std::fprintf(out, "%s{\"input\":{\"base_level\":%d,\"vit\":%d,\"int\":%d},\"max_hp\":%u,\"max_sp\":%u}", first_case ? "" : ",", level, n.vit, n.int_, sd->battle_status.max_hp, sd->battle_status.max_sp);
			first_case = false;
		}
	}
	std::fprintf(out, "]}}");
}

// VF-11: allocated VIT/INT changes followed by the real status_calc_pc (what pc_statusup runs after validation):
// records max and current HP/SP before and after, for a damaged character and a full-HP character, both directions.
static void vf11() {
	struct Case { int level, vit, int_, hp, sp, new_vit, new_int; };
	const Case cases[] = {
		{50, 30, 30, 100, 20, 31, 31}, {50, 30, 30, 0, 0, 31, 31},        // raise, damaged / empty
		{50, 30, 30, -1, -1, 31, 31},                                      // raise, full (-1 = start at max)
		{50, 30, 30, -1, -1, 10, 10}, {50, 30, 30, 50, 10, 10, 10},        // lower: full / below new max
		{50, 30, 30, 400, 100, 10, 10},                                    // lower: above new max (clamp)
		{99, 99, 99, -1, -1, 98, 98}, {1, 1, 1, -1, -1, 2, 2}};
	std::fprintf(out, "{\"id\":\"VF-11\",\"input\":{\"job\":\"Novice\",\"job_level\":1},\"output\":{\"cases\":[");
	uint32 id = 2000200;
	bool first_case = true;
	for (const Case& c : cases) {
		NoviceSpec n; n.base_level = c.level; n.vit = c.vit; n.int_ = c.int_;
		map_session_data* sd = make_novice(id++, n);
		sd->battle_status.hp = sd->base_status.hp = sd->status.hp = c.hp < 0 ? sd->battle_status.max_hp : c.hp;
		sd->battle_status.sp = sd->base_status.sp = sd->status.sp = c.sp < 0 ? sd->battle_status.max_sp : c.sp;
		uint32 before_max_hp = sd->battle_status.max_hp, before_max_sp = sd->battle_status.max_sp, before_hp = sd->battle_status.hp, before_sp = sd->battle_status.sp;
		sd->status.vit = c.new_vit; sd->status.int_ = c.new_int;
		status_calc_pc(sd, SCO_NONE);
		std::fprintf(out, "%s{\"input\":{\"base_level\":%d,\"vit\":%d,\"int\":%d,\"hp\":%u,\"sp\":%u,\"new_vit\":%d,\"new_int\":%d},"
			"\"before\":{\"max_hp\":%u,\"max_sp\":%u},\"after\":{\"max_hp\":%u,\"max_sp\":%u,\"hp\":%u,\"sp\":%u,\"char_hp\":%u,\"char_sp\":%u}}",
			first_case ? "" : ",", c.level, c.vit, c.int_, before_hp, before_sp, c.new_vit, c.new_int, before_max_hp, before_max_sp,
			sd->battle_status.max_hp, sd->battle_status.max_sp, sd->battle_status.hp, sd->battle_status.sp, sd->status.hp, sd->status.sp);
		first_case = false;
	}
	std::fprintf(out, "]}}");
}

// VF-05: equipment loadouts through the real equipment pass of status_calc_pc.
static void vf05() {
	const std::vector<int> loadouts[] = {{}, {1201}, {2301}, {1201, 2301}};
	NoviceSpec base; base.base_level = 10; base.job_level = 5; base.str = 20; base.agi = 15; base.vit = 12; base.int_ = 5; base.dex = 18; base.luk = 7;
	std::fprintf(out, "{\"id\":\"VF-05\",\"input\":{\"job\":\"Novice\",\"base_level\":10,\"job_level\":5,\"str\":20,\"agi\":15,\"vit\":12,\"int\":5,\"dex\":18,\"luk\":7},\"output\":{\"loadouts\":[");
	uint32 id = 2000300;
	for (size_t i = 0; i < 4; i++) {
		NoviceSpec n = base; n.items = loadouts[i];
		map_session_data* sd = make_novice(id++, n);
		std::fprintf(out, "%s{\"items\":[", i ? "," : "");
		for (size_t k = 0; k < n.items.size(); k++) std::fprintf(out, "%s%d", k ? "," : "", n.items[k]);
		std::fprintf(out, "],");
		dump_status("base_status", sd->base_status);
		std::fprintf(out, ",");
		dump_status("battle_status", sd->battle_status);
		std::fprintf(out, "}");
	}
	std::fprintf(out, "]}}");
}

// VF-23: refined Sword (1101, weapon_level 1, matk 0 in item_db) at refine 0/5/10 through the real equipment pass of
// status_calc_pc, then the real status_calc_bl_main MATK path (status.cpp ~6283-6361): status->matk_min/max include
// WeaponMATK = (lhw.matk + rhw.matk) +/- variance, where rhw.matk itself already carries the refine bonus (wa->matk +=
// info->bonus/100, non-bow only, status.cpp:3977) on top of the item's static matk. Demonstrates refine DOES move a
// player's live matk_min/matk_max, contrary to an earlier (incorrect) conclusion that only status_base_matk_min/max
// (the StatusMATK term alone) governs player MATK.
static void vf23() {
	const int refines[] = {0, 5, 10};
	NoviceSpec base; base.base_level = 10; base.job_level = 5; base.str = 20; base.agi = 15; base.vit = 12; base.int_ = 5; base.dex = 18; base.luk = 7; base.items = {1101};
	std::fprintf(out, "{\"id\":\"VF-23\",\"input\":{\"job\":\"Novice\",\"base_level\":10,\"job_level\":5,\"str\":20,\"agi\":15,\"vit\":12,\"int\":5,\"dex\":18,\"luk\":7,\"item\":1101},\"output\":{\"refines\":[");
	uint32 id = 2000400;
	for (size_t i = 0; i < 3; i++) {
		NoviceSpec n = base; n.item_refines = {refines[i]};
		map_session_data* sd = make_novice(id++, n);
		std::fprintf(out, "%s{\"refine\":%d,", i ? "," : "", refines[i]);
		dump_status("base_status", sd->base_status);
		std::fprintf(out, ",");
		dump_status("battle_status", sd->battle_status);
		std::fprintf(out, "}");
	}
	std::fprintf(out, "]}}");
}

// Spawns `mob_id` and samples real battle_calc_attack(BF_WEAPON) from `attacker` to `target` over 2000 seeded rolls.
// Normal-hit damage and critical damage are recorded separately; misses (flee/accuracy) are only counted.
// First non-critical hit's Renewal damage parts (intermediates of battle_calc_damage_parts), for locating divergence.
struct FirstParts { bool set = false; long long statusAtk = 0, weaponAtk = 0, equipAtk = 0, masteryAtk = 0, percentAtk = 0, damage = 0; };

static void sample_attacks(block_list* attacker, block_list* target, std::map<long long, int>& normal, std::map<long long, int>& crit, int& misses, FirstParts* parts = nullptr) {
	for (int i = 1; i <= 2000; i++) {
		generator.seed(i);
		Damage d = battle_calc_attack(BF_WEAPON, attacker, target, 0, 0, 0);
		long long total = d.damage + d.damage2;
		if (total < 1) misses++;
		else if (d.type == DMG_CRITICAL) crit[total]++;
		else {
			normal[total]++;
			if (parts && !parts->set) { parts->set = true; parts->statusAtk = d.statusAtk; parts->weaponAtk = d.weaponAtk; parts->equipAtk = d.equipAtk; parts->masteryAtk = d.masteryAtk; parts->percentAtk = d.percentAtk; parts->damage = total; }
		}
	}
}

static void print_histogram(const char* name, const std::map<long long, int>& h) {
	std::fprintf(out, "\"%s\":{", name);
	bool f = true;
	for (const auto& [dmg, count] : h) { std::fprintf(out, "%s\"%lld\":%d", f ? "" : ",", dmg, count); f = false; }
	std::fprintf(out, "}");
}

// VF-06: Novice with a Knife attacking real mobs (player -> mob weapon damage incl. size fix, element and mob DEF).
static void vf06() {
	NoviceSpec n; n.base_level = 10; n.job_level = 5; n.str = 20; n.agi = 15; n.vit = 12; n.int_ = 5; n.dex = 18; n.luk = 7; n.items = {1201};
	map_session_data* sd = make_novice(2000320, n);
	int16 m = map_mapname2mapid("prontera");
	std::fprintf(out, "{\"id\":\"VF-06\",\"input\":{\"attacker\":{\"base_level\":10,\"job_level\":5,\"str\":20,\"agi\":15,\"vit\":12,\"int\":5,\"dex\":18,\"luk\":7,\"items\":[1201]},\"samples\":2000},\"output\":{\"weapon_atkmods\":{\"small\":%d,\"medium\":%d,\"large\":%d},", sd->right_weapon.atkmods[SZ_SMALL], sd->right_weapon.atkmods[SZ_MEDIUM], sd->right_weapon.atkmods[SZ_BIG]);
	std::fprintf(out, "\"loaded_size_fix_db\":[");
	for (int w = 0; w <= 23; w++) {
		std::shared_ptr<s_sizefix_db> e = size_fix_db.find(w);
		std::fprintf(out, "%s{\"weapon\":%d,\"entry\":", w ? "," : "", w);
		if (e) std::fprintf(out, "[%d,%d,%d]}", e->small, e->medium, e->large); else std::fprintf(out, "null}");
	}
	std::fprintf(out, "],\"attacks\":[");
	const int mobs[] = {1002, 1007, 1013};
	for (int k = 0; k < 3; k++) {
		int32 gid = mob_once_spawn(nullptr, m, 0, 0, "--en--", mobs[k], 1, "", SZ_SMALL, AI_NONE);
		mob_data* md = map_id2md(gid);
		std::map<long long, int> normal, crit; int misses = 0; FirstParts parts;
		sample_attacks(sd, md, normal, crit, misses, &parts);
		std::fprintf(out, "%s{\"mob_id\":%d,\"misses_or_zero\":%d,\"first_normal_hit_parts\":{\"status_atk\":%lld,\"weapon_atk\":%lld,\"equip_atk\":%lld,\"mastery_atk\":%lld,\"percent_atk\":%lld,\"damage\":%lld},\"target\":{\"def\":%d,\"def2\":%d,\"size\":%d,\"element\":%d,\"level\":%d},",
			k ? "," : "", mobs[k], misses, parts.statusAtk, parts.weaponAtk, parts.equipAtk, parts.masteryAtk, parts.percentAtk, parts.damage,
			md->status.def, md->status.def2, md->status.size, md->status.def_ele, md->level);
		print_histogram("normal", normal); std::fprintf(out, ","); print_histogram("critical", crit);
		std::fprintf(out, "}");
	}
	std::fprintf(out, "]}}");
}

// VF-15: monsters attacking a Novice wearing a Cotton Shirt (hard DEF 10) - exercises def1 in the defense reduction.
static void vf15() {
	NoviceSpec n; n.base_level = 10; n.job_level = 5; n.str = 20; n.agi = 15; n.vit = 12; n.int_ = 5; n.dex = 18; n.luk = 7; n.items = {2301};
	map_session_data* sd = make_novice(2000340, n);
	int16 m = map_mapname2mapid("prontera");
	std::fprintf(out, "{\"id\":\"VF-15\",\"input\":{\"target\":{\"base_level\":10,\"job_level\":5,\"str\":20,\"agi\":15,\"vit\":12,\"int\":5,\"dex\":18,\"luk\":7,\"items\":[2301]},\"samples\":2000},\"output\":{");
	dump_status("target_battle_status", sd->battle_status);
	std::fprintf(out, ",\"attacks\":[");
	const int mobs[] = {1031, 1013};
	for (int k = 0; k < 2; k++) {
		int32 gid = mob_once_spawn(nullptr, m, 0, 0, "--en--", mobs[k], 1, "", SZ_SMALL, AI_NONE);
		mob_data* md = map_id2md(gid);
		std::map<long long, int> normal, crit; int misses = 0;
		sample_attacks(md, sd, normal, crit, misses);
		std::fprintf(out, "%s{\"mob_id\":%d,\"misses_or_zero\":%d,", k ? "," : "", mobs[k], misses);
		print_histogram("normal", normal); std::fprintf(out, ","); print_histogram("critical", crit);
		std::fprintf(out, "}");
	}
	std::fprintf(out, "]}}");
}

// VF-16: job coverage - every job class the generated progression data carries, at two (base, job) level pairs with fixed
// allocated stats and no equipment: six stats (base and battle), derived stats, max HP/SP and fist ASPD/motion.
static void vf16() {
	const int jobs[] = {JOB_NOVICE, JOB_SWORDMAN, JOB_MAGE, JOB_ARCHER, JOB_ACOLYTE, JOB_MERCHANT, JOB_THIEF, JOB_KNIGHT, JOB_PRIEST, JOB_WIZARD,
		JOB_BLACKSMITH, JOB_HUNTER, JOB_ASSASSIN, JOB_CRUSADER, JOB_MONK, JOB_SAGE, JOB_ROGUE, JOB_ALCHEMIST, JOB_BARD, JOB_DANCER, JOB_SUPER_NOVICE,
		JOB_GUNSLINGER, JOB_NINJA, JOB_TAEKWON, JOB_STAR_GLADIATOR, JOB_SOUL_LINKER, JOB_NOVICE_HIGH, JOB_SWORDMAN_HIGH, JOB_MAGE_HIGH, JOB_LORD_KNIGHT,
		JOB_HIGH_WIZARD, JOB_BABY, JOB_BABY_SWORDMAN, JOB_BABY_KNIGHT, JOB_RUNE_KNIGHT, JOB_WARLOCK, JOB_ARCH_BISHOP, JOB_SUMMONER, JOB_SUPER_BABY,
		JOB_DRAGON_KNIGHT, JOB_ARCH_MAGE, JOB_KAGEROU, JOB_REBELLION, JOB_HYPER_NOVICE};
	const int levels[][2] = {{1, 1}, {70, 10}};
	std::fprintf(out, "{\"id\":\"VF-16\",\"input\":{\"str\":30,\"agi\":20,\"vit\":40,\"int\":25,\"dex\":35,\"luk\":15},\"output\":{\"cases\":[");
	uint32 id = 2001000;
	bool first_case = true;
	for (int job : jobs) {
		for (const auto& lv : levels) {
			NoviceSpec n; n.job = job; n.base_level = lv[0]; n.job_level = lv[1]; n.str = 30; n.agi = 20; n.vit = 40; n.int_ = 25; n.dex = 35; n.luk = 15;
			map_session_data* sd = make_novice(id++, n);
			std::fprintf(out, "%s{\"job\":%d,\"base_level\":%d,\"job_level\":%d,", first_case ? "" : ",", job, lv[0], lv[1]);
			dump_status("base_status", sd->base_status);
			std::fprintf(out, ",");
			dump_status("battle_status", sd->battle_status);
			std::fprintf(out, "}");
			first_case = false;
		}
	}
	std::fprintf(out, "]}}");
}

// VF-17: attack motion per job and weapon type - the same jobs as VF-16 at base 1/job 1 with fixed stats, wearing no weapon
// or one representative one-handed weapon per weapon_type (real item_db entries).
static void vf17() {
	const int jobs[] = {JOB_NOVICE, JOB_SWORDMAN, JOB_MAGE, JOB_ARCHER, JOB_ACOLYTE, JOB_MERCHANT, JOB_THIEF, JOB_KNIGHT, JOB_PRIEST, JOB_WIZARD,
		JOB_BLACKSMITH, JOB_HUNTER, JOB_ASSASSIN, JOB_CRUSADER, JOB_MONK, JOB_SAGE, JOB_ROGUE, JOB_ALCHEMIST, JOB_BARD, JOB_DANCER, JOB_SUPER_NOVICE,
		JOB_GUNSLINGER, JOB_NINJA, JOB_TAEKWON, JOB_STAR_GLADIATOR, JOB_SOUL_LINKER, JOB_NOVICE_HIGH, JOB_SWORDMAN_HIGH, JOB_MAGE_HIGH, JOB_LORD_KNIGHT,
		JOB_HIGH_WIZARD, JOB_BABY, JOB_BABY_SWORDMAN, JOB_BABY_KNIGHT, JOB_RUNE_KNIGHT, JOB_WARLOCK, JOB_ARCH_BISHOP, JOB_SUMMONER, JOB_SUPER_BABY,
		JOB_DRAGON_KNIGHT, JOB_ARCH_MAGE, JOB_KAGEROU, JOB_REBELLION, JOB_HYPER_NOVICE};
	const int weapons[] = {0, 1201, 1101, 1301, 1401, 1501, 1601, 1701, 1801, 1901};
	std::fprintf(out, "{\"id\":\"VF-17\",\"input\":{\"base_level\":1,\"job_level\":1,\"str\":30,\"agi\":20,\"vit\":40,\"int\":25,\"dex\":35,\"luk\":15},\"output\":{\"cases\":[");
	uint32 id = 2002000;
	bool first_case = true;
	for (int job : jobs) {
		for (int weapon : weapons) {
			NoviceSpec n; n.job = job; n.str = 30; n.agi = 20; n.vit = 40; n.int_ = 25; n.dex = 35; n.luk = 15;
			if (weapon) n.items = {weapon};
			map_session_data* sd = make_novice(id++, n);
			std::fprintf(out, "%s{\"job\":%d,\"weapon_item\":%d,\"weapon_type\":%d,\"amotion\":%d,\"adelay\":%d,\"range\":%d,\"watk\":%d}", first_case ? "" : ",", job, weapon, (int)sd->weapontype1,
				sd->battle_status.amotion, sd->battle_status.adelay, sd->battle_status.rhw.range, sd->battle_status.rhw.atk);
			first_case = false;
		}
	}
	std::fprintf(out, "],\"cap_cases\":[");
	// ASPD cap: an absurd Increase AGI level pushes the unclamped amotion far below the configured max_aspd (190), so
	// the recorded amotion is the cap itself ((2000 - 190*10) * 2 / 2 = 100), not a formula result.
	const int cap_levels[] = {10, 40, 100};
	for (size_t i = 0; i < 3; i++) {
		NoviceSpec n; n.job = JOB_SWORDMAN; n.base_level = 99; n.job_level = 10; n.str = 99; n.agi = 99; n.vit = 99; n.int_ = 99; n.dex = 99; n.luk = 99;
		map_session_data* sd = make_novice(id++, n);
		status_change_start(sd, sd, SC_INCREASEAGI, 10000, cap_levels[i], 0, 0, 0, 120000, SCSTART_NOAVOID | SCSTART_NOICON | SCSTART_NOTICKDEF);
		status_calc_pc(sd, SCO_NONE);
		std::fprintf(out, "%s{\"job\":%d,\"increase_agi_level\":%d,\"agi\":%d,\"dex\":%d,\"amotion\":%d,\"adelay\":%d}", i ? "," : "", JOB_SWORDMAN, cap_levels[i], sd->battle_status.agi, sd->battle_status.dex, sd->battle_status.amotion, sd->battle_status.adelay);
	}
	std::fprintf(out, "]}}");
}

// Applies `type` twice via the real status_change_start and records what the reference does.
// deadline_moved: the status timer's absolute expiry changed between the two starts (a 50ms pause
// separates them), i.e. the second start re-armed the status; retained/refreshed is decided by that.
static void status_sequences(const char* id, sc_type type, const int seqs[][2], int count, uint32 first_id) {
	std::fprintf(out, "{\"id\":\"%s\",\"input\":{\"base_level\":40,\"job_level\":8,\"str\":20,\"agi\":30,\"vit\":25,\"int\":12,\"dex\":33,\"luk\":14,\"duration_ms\":120000,\"flag\":\"NOAVOID|NOICON|NOTICKDEF\"},\"output\":{\"sequences\":[", id);
	for (int i = 0; i < count; i++) {
		NoviceSpec spec; spec.base_level = 40; spec.job_level = 8; spec.str = 20; spec.agi = 30; spec.vit = 25; spec.int_ = 12; spec.dex = 33; spec.luk = 14;
		map_session_data* sd = make_novice(first_id + i, spec);
		std::fprintf(out, "%s{\"levels\":[%d,%d],\"steps\":[", i ? "," : "", seqs[i][0], seqs[i][1]);
		t_tick previous_deadline = 0;
		for (int j = 0; j < 2; j++) {
			if (j) std::this_thread::sleep_for(std::chrono::milliseconds(50));
			bool started = status_change_start(sd, sd, type, 10000, seqs[i][j], 0, 0, 0, 120000, SCSTART_NOAVOID | SCSTART_NOICON | SCSTART_NOTICKDEF);
			status_calc_pc(sd, SCO_NONE);
			status_change_entry* sce = sd->sc.getSCE(type);
			std::fprintf(out, "%s{\"returned\":%d,\"present\":%d", j ? "," : "", started, sce != nullptr);
			if (sce) {
				const TimerData* td = get_timer(sce->timer);
				t_tick deadline = td ? td->tick : 0;
				std::fprintf(out, ",\"val1\":%d,\"val2\":%d,\"remaining_ms_1000\":%lld", sce->val1, sce->val2, (long long)((deadline - gettick() + 500) / 1000 * 1000));
				if (j) std::fprintf(out, ",\"deadline_moved\":%d", deadline != previous_deadline);
				previous_deadline = deadline;
			}
			const status_data& b = sd->battle_status;
			std::fprintf(out, ",\"str\":%d,\"agi\":%d,\"vit\":%d,\"int\":%d,\"dex\":%d,\"luk\":%d,\"batk\":%d,\"hit\":%d,\"flee\":%d,\"flee2\":%d,\"cri\":%d,\"def2\":%d,\"mdef2\":%d,\"matk_min\":%d,\"matk_max\":%d,\"max_hp\":%u,\"max_sp\":%u,\"speed\":%d,\"amotion\":%d,\"adelay\":%d,\"dmotion\":%d}", b.str, b.agi, b.vit, b.int_, b.dex, b.luk, b.batk, b.hit, b.flee, b.flee2, b.cri, b.def2, b.mdef2, b.matk_min, b.matk_max, b.max_hp, b.max_sp, b.speed, b.amotion, b.adelay, b.dmotion);
		}
		status_change_end(sd, type); // real end path (same function the expiry timer calls)
		status_calc_pc(sd, SCO_NONE);
		const status_data& b = sd->battle_status;
		std::fprintf(out, "],\"after_end\":{\"present\":%d,\"str\":%d,\"agi\":%d,\"vit\":%d,\"int\":%d,\"dex\":%d,\"luk\":%d,\"batk\":%d,\"hit\":%d,\"flee\":%d,\"flee2\":%d,\"cri\":%d,\"def2\":%d,\"mdef2\":%d,\"matk_min\":%d,\"matk_max\":%d,\"max_hp\":%u,\"max_sp\":%u,\"speed\":%d,\"amotion\":%d,\"adelay\":%d,\"dmotion\":%d}}",
			sd->sc.getSCE(type) != nullptr, b.str, b.agi, b.vit, b.int_, b.dex, b.luk, b.batk, b.hit, b.flee, b.flee2, b.cri, b.def2, b.mdef2, b.matk_min, b.matk_max, b.max_hp, b.max_sp, b.speed, b.amotion, b.adelay, b.dmotion);
	}
	std::fprintf(out, "]}}");
}

static void vf07_vf08() {
	static const int seqs[][2] = {{10, 3}, {10, 10}, {3, 10}};
	status_sequences("VF-07", SC_BLESSING, seqs, 3, 2000010);
	std::fprintf(out, ",");
	status_sequences("VF-08", SC_INCREASEAGI, seqs, 3, 2000020);
}

// VF-14: what the real mob_db loader stores (mob 1002), then real battle_calc_attack (BF_WEAPON, no skill)
// from a spawned mob against the VF-01 Novice with the RNG reseeded per sample. The rAthena generator is
// seeded, never replaced, so the damage code runs unmodified.
static void vf14() {
	std::shared_ptr<s_mob_db> mob = mob_db.find(1002);
	std::fprintf(out, "{\"id\":\"VF-14\",\"input\":{\"mob_id\":1002,\"attack_mobs\":[1002,1031,1013],\"samples_per_mob\":2000,\"target\":\"VF-01 Novice\"},\"output\":{");
	dump_status("loaded_status", mob->status);
	map_session_data* target = make_novice(2000030);
	int16 m = map_mapname2mapid("prontera");
	std::fprintf(out, ",\"attacks\":[");
	const int mobs[] = {1002, 1031, 1013};
	for (int k = 0; k < 3; k++) {
		int32 gid = mob_once_spawn(nullptr, m, 0, 0, "--en--", mobs[k], 1, "", SZ_SMALL, AI_NONE);
		mob_data* md = map_id2md(gid);
		std::fprintf(out, "%s{\"mob_id\":%d,", k ? "," : "", mobs[k]);
		dump_status("loaded_status", mob_db.find(mobs[k])->status);
		std::fprintf(out, ",");
		dump_status("spawned_status", md->status);
		std::map<long long, int> histogram;
		int misses = 0;
		for (int i = 1; i <= 2000; i++) {
			generator.seed(i);
			Damage d = battle_calc_attack(BF_WEAPON, md, target, 0, 0, 0);
			if (d.damage + d.damage2 < 1) misses++;
			else histogram[d.damage + d.damage2]++;
		}
		std::fprintf(out, ",\"misses_or_zero\":%d,\"damage_histogram\":{", misses);
		bool firstBin = true;
		for (const auto& [dmg, count] : histogram) { std::fprintf(out, "%s\"%lld\":%d", firstBin ? "" : ",", dmg, count); firstBin = false; }
		std::fprintf(out, "}}");
	}
	std::fprintf(out, "]}}");
}


// Feature 16 data-pipeline evidence. Neither mode contains a formula: they only read what the real pinned loaders
// produced. RA_NAMES=<file with one script identifier per line> writes "name<TAB>value<TAB>c|p" per resolved
// constant/parameter (script_get_constant / script_get_parameter) to RA_NAMES_OUT. RA_ITEMS_OUT writes one canonical
// line per loaded item_db entry (sorted by id) so the C# item loader can be compared field by field.
static void dump_constants(const char* names_path, const char* out_path) {
	std::ifstream in(names_path);
	std::FILE* f = std::fopen(out_path, "w");
	std::string name;
	while (std::getline(in, name)) {
		if (name.empty()) continue;
		int64 value = 0;
		if (script_get_constant(name.c_str(), &value)) std::fprintf(f, "%s\t%lld\tc\n", name.c_str(), (long long)value);
		else if (script_get_parameter(name.c_str(), &value)) std::fprintf(f, "%s\t%lld\tp\n", name.c_str(), (long long)value);
	}
	std::fclose(f);
}


// One canonical line per loaded item group entry (sorted), the evidence for the C# item group loader.
static void dump_item_groups(const char* out_path) {
	std::vector<uint16> ids;
	for (auto& g : itemdb_group) ids.push_back(g.first);
	std::sort(ids.begin(), ids.end());
	std::FILE* f = std::fopen(out_path, "w");
	for (uint16 id : ids) {
		auto group = itemdb_group.find(id);
		std::vector<uint16> subs;
		for (auto& r : group->random) subs.push_back(r.first);
		std::sort(subs.begin(), subs.end());
		for (uint16 sub : subs) {
			auto random = group->random[sub];
			std::vector<uint32> indexes;
			for (auto& e : random->data) indexes.push_back(e.first);
			std::sort(indexes.begin(), indexes.end());
			for (uint32 index : indexes) {
				auto e = random->data[index];
				std::fprintf(f, "%u|%u|%d|%u|%u|%u|%u|%u|%u|%u|%d%d%d%d|%u|%u|%u|%u|%u|%u\n", (unsigned)id, (unsigned)sub, (int)random->algorithm, (unsigned)random->total_rate, (unsigned)index,
					(unsigned)e->nameid, (unsigned)e->rate, (unsigned)e->amount, (unsigned)e->duration, (unsigned)e->bound, e->isAnnounced, e->GUID, e->isStacked, e->isNamed,
					e->randomOptionGroup ? (unsigned)e->randomOptionGroup->id : 0u, (unsigned)e->refineMinimum, (unsigned)e->refineMaximum, (unsigned)e->minimumEnchantgrade, (unsigned)e->maximumEnchantgrade, 0u);
			}
		}
	}
	std::fclose(f);
}

static void dump_items(const char* out_path) {
	std::vector<std::shared_ptr<item_data>> items;
	for (auto& it : item_db) items.push_back(it.second);
	std::sort(items.begin(), items.end(), [](const auto& a, const auto& b) { return a->nameid < b->nameid; });
	std::FILE* f = std::fopen(out_path, "w");
	for (const auto& i : items) {
		std::string line;
		auto n = [&](long long v) { line += std::to_string(v); line += '|'; };
		n(i->nameid); line += i->name.c_str(); line += '|'; line += i->ename.c_str(); line += '|';
		n(i->type); n(i->subtype); n(i->value_buy); n(i->value_sell); n(i->weight); n(i->atk); n(i->matk); n(i->def); n(i->range); n(i->slots);
		n((long long)i->class_base[0]); n((long long)i->class_base[1]); n((long long)i->class_base[2]); n(i->class_upper); n(i->sex);
		n(i->equip); n(i->weapon_level); n(i->armor_level); n(i->elv); n(i->elvmax); n(i->flag.no_refine); n(i->flag.gradable);
		n(i->look); n(i->view_id); n(i->flag.buyingstore); n(i->flag.dead_branch); n(i->flag.group); n(i->flag.guid);
		n(i->flag.bindOnEquip); n(i->flag.broadcast); n(i->flag.delay_consume); n(i->flag.dropEffect);
		n(i->delay.duration); n(i->delay.sc); n(i->stack.amount); n(i->stack.inventory); n(i->stack.cart); n(i->stack.storage); n(i->stack.guild_storage);
		n(i->item_usage.override); n(i->item_usage.sitting); n(i->gm_lv_trade_override);
		n(i->flag.trade_restriction.drop); n(i->flag.trade_restriction.trade); n(i->flag.trade_restriction.trade_partner);
		n(i->flag.trade_restriction.sell); n(i->flag.trade_restriction.cart); n(i->flag.trade_restriction.storage);
		n(i->flag.trade_restriction.guild_storage); n(i->flag.trade_restriction.mail); n(i->flag.trade_restriction.auction);
		n(i->script != nullptr); n(i->equip_script != nullptr); n(i->unequip_script != nullptr);
		line.back() = '\n';
		std::fputs(line.c_str(), f);
	}
	std::fclose(f);
}


// ---- Feature 16 inventory evidence (VF-20..VF-22): real pc_additem / status_calc_weight / pc_updateweightstatus ----
struct InvRow { int nameid; int amount; int bound = 0; unsigned expire = 0; unsigned long long unique = 0; unsigned card0 = 0; };

static void fill_inventory(map_session_data* sd, const std::vector<InvRow>& rows, int slots) {
	for (int i = 0; i < MAX_INVENTORY; i++) { memset(&sd->inventory.u.items_inventory[i], 0, sizeof(struct item)); sd->inventory_data[i] = nullptr; }
	int slot = 0;
	for (const InvRow& r : rows) {
		struct item& row = sd->inventory.u.items_inventory[slot];
		row.nameid = r.nameid; row.amount = r.amount; row.identify = 1; row.bound = r.bound; row.expire_time = r.expire; row.unique_id = r.unique; row.card[0] = r.card0;
		sd->inventory_data[slot] = itemdb_search(r.nameid);
		slot++;
	}
	sd->status.inventory_slots = slots;
	status_calc_weight(sd, static_cast<e_status_calc_weight_opt>(CALCWT_ITEM | CALCWT_MAXBONUS));
}

template <typename Pred> static int find_item(Pred pred) {
	int best = 0;
	for (auto& it : item_db) if (pred(it.second.get()) && (best == 0 || (int)it.second->nameid < best)) best = (int)it.second->nameid;
	return best;
}

static void dump_rows(map_session_data* sd) {
	std::fprintf(out, "\"rows\":[");
	bool firstRow = true;
	for (int i = 0; i < MAX_INVENTORY; i++) {
		const struct item& row = sd->inventory.u.items_inventory[i];
		if (!row.nameid) continue;
		std::fprintf(out, "%s{\"slot\":%d,\"nameid\":%d,\"amount\":%d,\"bound\":%d,\"expire\":%u,\"unique\":%s}", firstRow ? "" : ",", i, (int)row.nameid, (int)row.amount, (int)row.bound, (unsigned)row.expire_time, row.unique_id ? "true" : "false");
		firstRow = false;
	}
	std::fprintf(out, "]");
}

// VF-20: base weight limit per job and STR through the real status_calc_pc -> status_calc_weight, plus the weight-related battle config.
static void vf20() {
	std::fprintf(out, "{\"id\":\"VF-20\",\"input\":{\"jobs\":\"representative first/second/third/expanded jobs\",\"str\":[1,50,99]},\"output\":{\"config\":{");
	std::fprintf(out, "\"natural_heal_weight_rate\":%d,\"major_overweight_rate\":%d,\"open_box_weight_rate\":%d,\"max_cart_weight\":%d,\"item_use_interval\":%d,\"cashfood_use_interval\":%d,\"item_first_get_time\":%d,\"item_second_get_time\":%d,\"item_third_get_time\":%d,\"mvp_item_first_get_time\":%d,\"mvp_item_second_get_time\":%d,\"mvp_item_third_get_time\":%d,\"flooritem_lifetime\":%d,\"first_attack_loot_bonus\":%d,\"drop_rate_cap\":%d,\"item_stacking\":%d,\"default_bind_on_equip\":%d,\"allow_bound_sell\":%d,\"inventory_base\":%d,\"max_inventory\":%d,\"max_amount\":%d,\"max_storage\":%d},\"cases\":[",
		battle_config.natural_heal_weight_rate, battle_config.major_overweight_rate, battle_config.open_box_weight_rate, battle_config.max_cart_weight, battle_config.item_use_interval, battle_config.cashfood_use_interval,
		battle_config.item_first_get_time, battle_config.item_second_get_time, battle_config.item_third_get_time, battle_config.mvp_item_first_get_time, battle_config.mvp_item_second_get_time, battle_config.mvp_item_third_get_time,
		battle_config.flooritem_lifetime, battle_config.first_attack_loot_bonus, battle_config.drop_rate_cap, battle_config.item_stacking, battle_config.default_bind_on_equip, battle_config.allow_bound_sell, INVENTORY_BASE_SIZE, MAX_INVENTORY, MAX_AMOUNT, MAX_STORAGE);
	const int jobs[] = {JOB_NOVICE, JOB_SWORDMAN, JOB_MAGE, JOB_ARCHER, JOB_ACOLYTE, JOB_MERCHANT, JOB_THIEF, JOB_KNIGHT, JOB_BLACKSMITH, JOB_SUPER_NOVICE, JOB_GUNSLINGER, JOB_NINJA, JOB_RUNE_KNIGHT, JOB_SUMMONER};
	uint32 id = 2001000;
	bool firstCase = true;
	for (int job : jobs) for (int str : {1, 50, 99}) {
		NoviceSpec n; n.job = job; n.str = str;
		map_session_data* sd = make_novice(id++, n);
		std::fprintf(out, "%s{\"job\":%d,\"str\":%d,\"max_weight\":%d}", firstCase ? "" : ",", job, str, (int)sd->max_weight);
		firstCase = false;
	}
	std::fprintf(out, "]}}");
}

// VF-21: real pc_additem decisions: capacity, weight, stacking, binding/rental/card mismatch, stack limits, unique ids.
static void vf21() {
	const int potion = 501, knife = 1201;
	const int ancilla = find_item([](item_data* d) { return d->stack.inventory && d->stack.amount == 3; });
	const int light = find_item([](item_data* d) { return itemdb_isstackable2(d) && d->weight == 0 && !d->stack.inventory && !d->flag.guid && d->type == IT_ETC; });
	const int unique = find_item([](item_data* d) { return d->flag.guid && itemdb_isstackable2(d); });
	std::fprintf(out, "{\"id\":\"VF-21\",\"input\":{\"potion\":%d,\"knife\":%d,\"stack_limited\":%d,\"weightless_stackable\":%d,\"unique_stackable\":%d},\"output\":{\"cases\":[", potion, knife, ancilla, light, unique);
	struct Case { const char* name; int str; int slots; long long max_weight; std::vector<InvRow> rows; InvRow add; int amount; };
	std::vector<InvRow> full10; for (int i = 0; i < 10; i++) full10.push_back({knife, 1});
	const std::vector<Case> cases = {
		{"empty-add-stackable", 99, 100, -1, {}, {potion, 0}, 1},
		{"merge-into-existing-stack", 99, 100, -1, {{potion, 30}}, {potion, 0}, 5},
		{"non-stackable-single", 99, 100, -1, {}, {knife, 0}, 1},
		{"non-stackable-multiple-amount", 99, 100, -1, {}, {knife, 0}, 3},
		{"overamount-argument", 99, 100, 99999999, {}, {light, 0}, 30001},
		{"overamount-merge", 99, 100, 99999999, {{light, 29999}}, {light, 0}, 2},
		{"exactly-max-amount", 99, 100, 99999999, {{light, 29999}}, {light, 0}, 1},
		{"stack-limit-argument", 99, 100, 99999999, {}, {ancilla, 0}, 4},
		{"stack-limit-merge", 99, 100, 99999999, {{ancilla, 2}}, {ancilla, 0}, 2},
		{"stack-limit-merge-ok", 99, 100, 99999999, {{ancilla, 2}}, {ancilla, 0}, 1},
		{"overweight", 1, 100, 100, {}, {potion, 0}, 2},
		{"exact-weight-ok", 1, 100, 140, {}, {potion, 0}, 2},
		{"over-item-slots-full", 99, 10, -1, full10, {potion, 0}, 1},
		{"merge-when-slots-full", 99, 10, -1, [&] { auto v = full10; v[0] = {potion, 5}; return v; }(), {potion, 0}, 1},
		{"bound-mismatch-new-row", 99, 100, -1, {{potion, 5, 1}}, {potion, 0, 0}, 1},
		{"bound-match-merges", 99, 100, -1, {{potion, 5, 1}}, {potion, 0, 1}, 1},
		{"rental-never-merges", 99, 100, -1, {{potion, 5}}, {potion, 0, 0, 4102444800u}, 1},
		{"existing-rental-never-merged-into", 99, 100, -1, {{potion, 5, 0, 4102444800u}}, {potion, 0}, 1},
		{"card-mismatch-new-row", 99, 100, -1, {{potion, 5, 0, 0, 0, 4001}}, {potion, 0}, 1},
		{"unique-id-assigned", 99, 100, -1, {}, {unique, 0}, 1},
		{"merge-slot-beyond-limit", 99, 5, -1, [&] { std::vector<InvRow> v(7, InvRow{knife, 1}); v[6] = {potion, 5}; return v; }(), {potion, 0}, 1},
		{"invalid-amount", 99, 100, -1, {}, {potion, 0}, 0},
	};
	uint32 id = 2002000;
	bool firstCase = true;
	for (const Case& c : cases) {
		NoviceSpec n; n.str = c.str;
		map_session_data* sd = make_novice(id++, n);
		fill_inventory(sd, c.rows, c.slots);
		if (c.max_weight >= 0) sd->max_weight = (int)c.max_weight;
		struct item it{}; it.nameid = c.add.nameid; it.identify = 1; it.bound = c.add.bound; it.expire_time = c.add.expire; it.card[0] = c.add.card0; it.unique_id = c.add.unique;
		const int before_weight = sd->weight;
		const int result = (int)pc_additem(sd, &it, c.amount, LOG_TYPE_NONE);
		std::fprintf(out, "%s{\"name\":\"%s\",\"in\":{\"str\":%d,\"slots\":%d,\"max_weight\":%lld,\"amount\":%d,\"add\":{\"nameid\":%d,\"bound\":%d,\"expire\":%u,\"unique\":%llu,\"card0\":%u},\"rows\":[", firstCase ? "" : ",", c.name, c.str, c.slots, c.max_weight, c.amount, c.add.nameid, c.add.bound, c.add.expire, c.add.unique, c.add.card0);
		for (size_t r = 0; r < c.rows.size(); r++) std::fprintf(out, "%s{\"nameid\":%d,\"amount\":%d,\"bound\":%d,\"expire\":%u,\"unique\":%llu,\"card0\":%u}", r ? "," : "", c.rows[r].nameid, c.rows[r].amount, c.rows[r].bound, c.rows[r].expire, c.rows[r].unique, c.rows[r].card0);
		std::fprintf(out, "]},\"result\":%d,\"weight_before\":%d,\"weight_after\":%d,\"max_weight\":%d,\"free_slots\":%d,", result, before_weight, (int)sd->weight, (int)sd->max_weight, (int)pc_inventoryblank(sd));
		dump_rows(sd);
		std::fprintf(out, "}");
		firstCase = false;
	}
	std::fprintf(out, "]}}");
}

// VF-22: weight status thresholds and hysteresis through the real pc_updateweightstatus (SC_WEIGHT50 / SC_WEIGHT90, regen overweight).
static void vf22() {
	std::fprintf(out, "{\"id\":\"VF-22\",\"input\":{\"max_weight\":1000,\"weights\":[0,699,700,701,899,900,1000,899,699,0]},\"output\":{\"steps\":[");
	map_session_data* sd = make_novice(2003000);
	sd->max_weight = 1000;
	bool firstStep = true;
	for (int w : {0, 699, 700, 701, 899, 900, 1000, 899, 699, 0}) {
		sd->weight = w;
		pc_updateweightstatus(*sd);
		std::fprintf(out, "%s{\"weight\":%d,\"percent\":%d,\"weight50\":%s,\"weight90\":%s,\"overweight\":%s}", firstStep ? "" : ",", w, (int)pc_getpercentweight(*sd),
			sd->sc.getSCE(SC_WEIGHT50) ? "true" : "false", sd->sc.getSCE(SC_WEIGHT90) ? "true" : "false", sd->regen.state.overweight ? "true" : "false");
		firstStep = false;
	}
	std::fprintf(out, "]}}");
}

void ra_reference_run() {
	if (const char* names = std::getenv("RA_NAMES")) {
		dump_constants(names, std::getenv("RA_NAMES_OUT"));
		dump_items(std::getenv("RA_ITEMS_OUT"));
		if (const char* groups = std::getenv("RA_GROUPS_OUT")) dump_item_groups(groups);
		std::exit(0);
	}
	const char* path = std::getenv("RA_OUT");
	out = std::fopen(path ? path : "reference.json", "w");
#ifdef RENEWAL
	const int renewal = 1;
#else
	const int renewal = 0;
#endif
	std::fprintf(out, "{\"renewal_compiled\":%d,\"scenarios\":[", renewal);
	vf01();
	std::fprintf(out, ",");
	vf02_vf03();
	std::fprintf(out, ",");
	vf04();
	std::fprintf(out, ",");
	vf11();
	std::fprintf(out, ",");
	vf05();
	std::fprintf(out, ",");
	vf23();
	std::fprintf(out, ",");
	vf06();
	std::fprintf(out, ",");
	vf15();
	std::fprintf(out, ",");
	vf16();
	std::fprintf(out, ",");
	vf17();
	std::fprintf(out, ",");
	vf07_vf08();
	std::fprintf(out, ",");
	vf14();
	std::fprintf(out, ",");
	vf20();
	std::fprintf(out, ",");
	vf21();
	std::fprintf(out, ",");
	vf22();
	std::fprintf(out, "]}\n");
	std::fclose(out);
	std::exit(0);
}

} // namespace rathena::server_map
