namespace Athena.Net.MapServer.Gameplay.Rules;

// Pinned status_calc_pc's own before/after diff (status.cpp:6580-6652): after ANY recalculation (equip/unequip,
// refine, enchant grade, card insert, buff start/end, stat allocation, ...) the pinned source compares every
// derived field against its PREVIOUS value and sends clif_updatestatus only for what actually changed - never an
// unconditional full resend. This is the reusable SERVER-SIDE half of that mechanism: a pure comparison with no
// packet construction, no SP_* var-id mapping, and no wire I/O whatsoever.
//
// PROTOCOL-PENDING-CAPTURE (not implemented client-side, intentionally): `ai/iro-2026-wire.md`'s own capture
// evidence proves the 0x00B0 (ZC_PAR_CHANGE) wire LAYOUT and several SP_* var ids genuinely exist and are sent
// after a DIFFERENT trigger (stat-point allocation) - but that capture's own var-id annotations do not agree
// internally with the pinned _sp enum (map.hpp:504-505: SP_ATK1=41/SP_ATK2=42/SP_MATK1=43/SP_MATK2=44/
// SP_DEF1=45/SP_DEF2=46/SP_MDEF1=47/SP_MDEF2=48/SP_HIT=49/SP_FLEE1=50/SP_FLEE2=51/SP_CRITICAL=52/SP_ASPD=53) -
// e.g. the capture doc labels var 45 "DEF2" where the pinned enum says var 45 is SP_DEF1. Reusing an
// internally-inconsistent mapping for a NEW trigger (equip/unequip) this project has never captured at all would
// risk compounding that discrepancy into a genuinely wrong wire projection, which is exactly what the "never
// invent packet layouts without capture evidence" rule exists to prevent. This type therefore deliberately stops
// at "which named stats changed and by how much" - resolving each DerivedStatField to a concrete SP_* var id and
// building the actual 0x00B0 packets is left for whoever captures the real equip/unequip wire sequence.
//
// Exact captures still needed (see docs/gameplay/feature-16-execution-plan.md's "Outstanding iRO captures"):
// equip Knife (1201, no ATK-affecting refine/cards) while already wearing Cotton_Shirt (2301), and the matching
// unequip, each with the COMPLETE server-to-client packet sequence (ids, lengths, field values, exact order) -
// resolving: which DerivedStatField actually gets a 0x00B0 resend after an equip/unequip specifically (not just
// after stat allocation), the var id each maps to (confirming or correcting the capture-doc/pinned-enum mismatch
// above), whether MaxHp/MaxSp ever resend here too (ai/iro-2026-wire.md flags that as unresolved even for the
// allocation flow), and the relative ordering against the existing proven equip ack (0x0998 response)/appearance
// (clif_changelook family) packets this project already sends.
public enum DerivedStatField { BaseAttack, Hit, Flee, PerfectFlee, Critical, SoftDefense, SoftMagicDefense, HardDefense, MagicAttackMin, MagicAttackMax }

public readonly record struct DerivedStatChange(DerivedStatField Field, int Before, int After);

public static class DerivedStatComparer
{
    // Every field whose Before/After values differ, in DerivedStatField declaration order (matching the pinned
    // source's own field-by-field comparison order, status.cpp:6580-6652, for whenever a future capture settles
    // the real equip/unequip cascade order and this list needs to be projected onto actual packets).
    public static IReadOnlyList<DerivedStatChange> Diff(DerivedStats before, DerivedStats after)
    {
        var changes = new List<DerivedStatChange>();
        void Check(DerivedStatField field, int previous, int current) { if (previous != current) changes.Add(new(field, previous, current)); }
        Check(DerivedStatField.BaseAttack, before.BaseAttack, after.BaseAttack);
        Check(DerivedStatField.Hit, before.Hit, after.Hit);
        Check(DerivedStatField.Flee, before.Flee, after.Flee);
        Check(DerivedStatField.PerfectFlee, before.PerfectFlee, after.PerfectFlee);
        Check(DerivedStatField.Critical, before.Critical, after.Critical);
        Check(DerivedStatField.SoftDefense, before.SoftDefense, after.SoftDefense);
        Check(DerivedStatField.SoftMagicDefense, before.SoftMagicDefense, after.SoftMagicDefense);
        Check(DerivedStatField.HardDefense, before.HardDefense, after.HardDefense);
        Check(DerivedStatField.MagicAttackMin, before.MagicAttackMin, after.MagicAttackMin);
        Check(DerivedStatField.MagicAttackMax, before.MagicAttackMax, after.MagicAttackMax);
        return changes;
    }
}
