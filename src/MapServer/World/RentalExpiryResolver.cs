using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World;

// Pinned pc_inventory_rentals (pc.cpp:1200-1225): scans every inventory row for a non-zero
// expire_time already in the past and durably deletes it. Pinned source calls this exactly once,
// right after a character's inventory is loaded (pc_reg_received/login), plus from a scheduled
// in-process timer that re-announces the remaining time and re-checks every rental row while the
// character stays connected (pc_inventory_rental_add). This project reproduces the login-time
// check faithfully; the periodic re-announce timer is not modeled (no observable gameplay
// difference for an item that is still non-expired - only the clif_rental_time reminder packet
// would differ, and that packet has no capture evidence yet - see ai/iro-2026-wire.md). A rental
// that expires while already logged in is instead caught the next time this resolver runs (the
// next login/reconnect), a narrow, documented timing difference from continuously-ticking pinned
// source, never a silent skip.
public static class RentalExpiryResolver
{
    public readonly record struct ExpiredRental(uint DurableId, int ItemId, uint Amount);

    // Pure: given the current snapshot and "now" (seconds since epoch, matching the pinned
    // time_t/expire_time unit - see InventoryItemRow.ExpireTime's own doc comment), returns every
    // row whose ExpireTime is non-zero and already due. ExpireTime == 0 means "not a rental" -
    // pinned pc_inventory_rentals's own `if (expire_time == 0) continue;` guard.
    public static IReadOnlyList<ExpiredRental> FindExpired(CharacterInventorySnapshot snapshot, uint nowUnixSeconds)
    {
        List<ExpiredRental>? expired = null;
        foreach (var item in snapshot.Items)
        {
            var expireTime = item.Details.ExpireTime;
            if (expireTime == 0 || expireTime > nowUnixSeconds) continue;
            (expired ??= []).Add(new ExpiredRental(item.DurableId, item.ItemId, item.Amount));
        }
        return expired ?? (IReadOnlyList<ExpiredRental>)[];
    }
}
