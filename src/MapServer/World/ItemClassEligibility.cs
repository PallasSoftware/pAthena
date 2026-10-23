using Athena.Net.MapServer.Generated.Jobs;
using Athena.Net.MapServer.World.Items;

namespace Athena.Net.MapServer.World;

// The job/tier half of pinned pc_isequip and pc_isUseitem, shared by equipping and using an item.
public static class ItemClassEligibility
{
    // Pinned ITEMJ_* (itemdb.hpp:213-221) and the job tier bits GeneratedJobEquipClass.Tier carries (upper 1, baby 2,
    // third 4, fourth 8).
    private const uint ItemjNormal = 0x01, ItemjUpper = 0x02, ItemjBaby = 0x04, ItemjThird = 0x08, ItemjThirdUpper = 0x10, ItemjThirdBaby = 0x20, ItemjFourth = 0x40;
    private const byte Upper = 1, Baby = 2, Third = 4, Fourth = 8;

    // pc_job_can_use_item (pc.cpp:1771-1790) then pc_isItemClass (RENEWAL branch). A job GeneratedJobEquipClass has no
    // entry for (the "2" appearance variants) can use nothing, like pc_authok's invalid-class handling.
    public static bool CanUse(ItemMetadata meta, JobClass jobClass)
    {
        if (!GeneratedJobEquipClass.TryGet(jobClass, out var job)) return false;
        var classBase = job.ClassBaseIndex switch { 0 => meta.ClassBase0, 1 => meta.ClassBase1, _ => meta.ClassBase2 };
        if ((classBase & (1UL << job.FirstMask)) == 0) return false;

        var upper = meta.ClassUpper;
        var t = job.Tier;
        bool Has(byte bit) => (t & bit) != 0;
        return ((upper & ItemjNormal) != 0 && t == 0)
            || ((upper & ItemjUpper) != 0 && Has(Upper) && !Has(Third))
            || ((upper & ItemjBaby) != 0 && Has(Baby) && !Has(Third))
            || ((upper & ItemjThird) != 0 && Has(Third) && !Has(Upper) && !Has(Baby) && !Has(Fourth))
            || ((upper & ItemjThirdUpper) != 0 && Has(Third) && Has(Upper) && !Has(Fourth))
            || ((upper & ItemjThirdBaby) != 0 && Has(Third) && Has(Baby) && !Has(Fourth))
            || ((upper & ItemjFourth) != 0 && Has(Fourth));
    }
}
