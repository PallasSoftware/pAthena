using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World;

// Pinned pc_identifyall (pc.cpp:5664-5679): every inventory row that is not yet identified
// qualifies - unlike repair's pet-egg exclusion, identify has no special-case exclusion at all.
// Pure resolver only: the single-item identify-list UI (Magnifier use -> client identify list ->
// player picks one -> request packet) needs a client packet with no capture evidence yet (see
// ai/iro-2026-wire.md); this primitive exists so that future call site (and the already-reachable
// bulk identifyall NPC-script command, Feature 20) has a durable mutation to invoke once it
// resolves which item(s) to identify, without Feature 16 inventing a packet to get there.
public static class ItemIdentifyResolver
{
    public readonly record struct UnidentifiedItem(uint DurableId, int ItemId, uint Amount);

    public static IReadOnlyList<UnidentifiedItem> FindUnidentified(CharacterInventorySnapshot snapshot)
    {
        List<UnidentifiedItem>? unidentified = null;
        foreach (var item in snapshot.Items)
        {
            if (item.Identified) continue;
            (unidentified ??= []).Add(new UnidentifiedItem(item.DurableId, item.ItemId, item.Amount));
        }
        return unidentified ?? (IReadOnlyList<UnidentifiedItem>)[];
    }
}
