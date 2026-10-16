// Development-only scenario driver compiled INTO the pinned rAthena map-server.
// It only sets up inputs and calls real rAthena functions; no formulas live here.
#include <cstdio>
#include <cstdlib>
#include <cstring>

#include "battle.hpp"
#include "map.hpp"
#include "mob.hpp"
#include "pc.hpp"
#include "status.hpp"
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
	num("def", s.def); num("def2", s.def2); num("mdef", s.mdef); num("mdef2", s.mdef2);
	num("hit", s.hit); num("flee", s.flee); num("flee2", s.flee2); num("cri", s.cri);
	num("amotion", s.amotion); num("adelay", s.adelay); num("speed", s.speed);
	num("max_hp", s.max_hp); num("max_sp", s.max_sp); num("hp", s.hp); num("sp", s.sp);
	std::fprintf(out, "}");
}

static map_session_data* make_novice(uint32 id) {
	map_session_data* sd = new map_session_data();
	pc_setnewpc(sd, id, id + 100000, 0, gettick(), SEX_MALE, 0);
	sd->m = map_mapname2mapid("prontera"); // plain map, no PvP/GvG/BG flags; player is not added to the grid
	sd->status.class_ = JOB_NOVICE;
	sd->class_ = pc_jobid2mapid(JOB_NOVICE);
	sd->status.base_level = 1; sd->status.job_level = 1;
	sd->status.str = sd->status.agi = sd->status.vit = sd->status.int_ = sd->status.dex = sd->status.luk = 1;
	sd->status.max_hp = 40; sd->status.hp = 40; sd->status.max_sp = 11; sd->status.sp = 11;
	memset(&sd->equip_index, -1, sizeof(sd->equip_index));
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

// VF-07: real status_change_start applied twice on the same target (Blessing).
static void vf07() {
	static const int seqs[][2] = {{10, 3}, {10, 10}, {3, 10}};
	uint32 id = 2000010;
	std::fprintf(out, "{\"id\":\"VF-07\",\"input\":{\"duration_ms\":120000,\"flag\":\"NOAVOID|NOICON|NOTICKDEF\"},\"output\":{\"sequences\":[");
	for (int i = 0; i < 3; i++) {
		map_session_data* sd = make_novice(id++);
		std::fprintf(out, "%s{\"levels\":[%d,%d],\"steps\":[", i ? "," : "", seqs[i][0], seqs[i][1]);
		for (int j = 0; j < 2; j++) {
			bool started = status_change_start(sd, sd, SC_BLESSING, 10000, seqs[i][j], 0, 0, 0, 120000, SCSTART_NOAVOID | SCSTART_NOICON | SCSTART_NOTICKDEF);
			status_calc_pc(sd, SCO_NONE);
			status_change_entry* sce = sd->sc.getSCE(SC_BLESSING);
			std::fprintf(out, "%s{\"returned\":%d,\"present\":%d", j ? "," : "", started, sce != nullptr);
			if (sce) std::fprintf(out, ",\"val1\":%d,\"val2\":%d", sce->val1, sce->val2);
			std::fprintf(out, ",\"str\":%d,\"int\":%d,\"dex\":%d,\"hit\":%d}", sd->battle_status.str, sd->battle_status.int_, sd->battle_status.dex, sd->battle_status.hit);
		}
		std::fprintf(out, "]}");
	}
	std::fprintf(out, "]}}");
}

// VF-14 (loaded-values part only): what the real mob_db loader stores for Poring (1002).
static void vf14() {
	std::shared_ptr<s_mob_db> mob = mob_db.find(1002);
	std::fprintf(out, "{\"id\":\"VF-14\",\"input\":{\"mob_id\":1002},\"output\":{");
	dump_status("loaded_status", mob->status);
	std::fprintf(out, "}}");
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
	vf07();
	std::fprintf(out, ",");
	vf14();
	std::fprintf(out, "]}\n");
	std::fclose(out);
	std::exit(0);
}

} // namespace rathena::server_map
