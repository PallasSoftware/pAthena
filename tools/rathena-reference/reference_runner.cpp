// Development-only scenario driver compiled INTO the pinned rAthena map-server.
// It only sets up inputs and calls real rAthena functions; no formulas live here.
#include <cstdio>
#include <cstdlib>
#include <chrono>
#include <cstring>
#include <map>
#include <vector>
#include <memory>
#include <thread>

#include "battle.hpp"
#include "clif.hpp"
#include "itemdb.hpp"
#include "map.hpp"
#include "mob.hpp"
#include "pc.hpp"
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

struct NoviceSpec { int base_level = 1, job_level = 1, str = 1, agi = 1, vit = 1, int_ = 1, dex = 1, luk = 1; std::vector<int> items; };

static map_session_data* make_novice(uint32 id, const NoviceSpec& n = NoviceSpec()) {
	map_session_data* sd = new map_session_data();
	pc_setnewpc(sd, id, id + 100000, 0, gettick(), SEX_MALE, 0);
	sd->m = map_mapname2mapid("prontera"); // plain map, no PvP/GvG/BG flags; player is not added to the grid
	sd->status.class_ = JOB_NOVICE;
	sd->class_ = pc_jobid2mapid(JOB_NOVICE);
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
			std::fprintf(out, ",\"str\":%d,\"agi\":%d,\"vit\":%d,\"int\":%d,\"dex\":%d,\"luk\":%d,\"batk\":%d,\"hit\":%d,\"flee\":%d,\"flee2\":%d,\"cri\":%d,\"def2\":%d,\"mdef2\":%d,\"max_hp\":%u,\"max_sp\":%u,\"speed\":%d,\"amotion\":%d,\"adelay\":%d,\"dmotion\":%d}", b.str, b.agi, b.vit, b.int_, b.dex, b.luk, b.batk, b.hit, b.flee, b.flee2, b.cri, b.def2, b.mdef2, b.max_hp, b.max_sp, b.speed, b.amotion, b.adelay, b.dmotion);
		}
		status_change_end(sd, type); // real end path (same function the expiry timer calls)
		status_calc_pc(sd, SCO_NONE);
		const status_data& b = sd->battle_status;
		std::fprintf(out, "],\"after_end\":{\"present\":%d,\"str\":%d,\"agi\":%d,\"vit\":%d,\"int\":%d,\"dex\":%d,\"luk\":%d,\"batk\":%d,\"hit\":%d,\"flee\":%d,\"flee2\":%d,\"cri\":%d,\"def2\":%d,\"mdef2\":%d,\"max_hp\":%u,\"max_sp\":%u,\"speed\":%d,\"amotion\":%d,\"adelay\":%d,\"dmotion\":%d}}",
			sd->sc.getSCE(type) != nullptr, b.str, b.agi, b.vit, b.int_, b.dex, b.luk, b.batk, b.hit, b.flee, b.flee2, b.cri, b.def2, b.mdef2, b.max_hp, b.max_sp, b.speed, b.amotion, b.adelay, b.dmotion);
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

void ra_reference_run() {
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
	vf06();
	std::fprintf(out, ",");
	vf15();
	std::fprintf(out, ",");
	vf07_vf08();
	std::fprintf(out, ",");
	vf14();
	std::fprintf(out, "]}\n");
	std::fclose(out);
	std::exit(0);
}

} // namespace rathena::server_map
