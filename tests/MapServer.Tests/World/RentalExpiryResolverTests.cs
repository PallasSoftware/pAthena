using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class RentalExpiryResolverTests
{
    private static CharacterInventoryItem RentalRow(uint durableId, uint expireTime) =>
        new(durableId, SlotIndex: durableId, ItemId: 501, Amount: 1, Equip: 0, Identified: true, Refine: 0, Favorite: 0, Bound: 0,
            Detail: new ItemInstanceDetail(0, 0, 0, 0, 0, 0, default, default, default, default, default, expireTime, 0, 0, 0));

    private static CharacterInventoryItem NonRentalRow(uint durableId) =>
        new(durableId, SlotIndex: durableId, ItemId: 6008, Amount: 1, Equip: 0, Identified: true, Refine: 0, Favorite: 0, Bound: 0);

    [Fact]
    public void ExpireTimeZero_IsNeverConsideredARental()
    {
        var snapshot = new CharacterInventorySnapshot([NonRentalRow(1)]);

        Assert.Empty(RentalExpiryResolver.FindExpired(snapshot, nowUnixSeconds: 999_999));
    }

    [Fact]
    public void ExpireTimeInThePast_IsExpired()
    {
        var snapshot = new CharacterInventorySnapshot([RentalRow(1, expireTime: 1000)]);

        var expired = RentalExpiryResolver.FindExpired(snapshot, nowUnixSeconds: 1001);

        var single = Assert.Single(expired);
        Assert.Equal(1u, single.DurableId);
        Assert.Equal(501, single.ItemId);
    }

    // Pinned pc.cpp:1210: `expire_time <= time(nullptr)` - exactly-equal is ALSO expired, not just strictly past.
    [Fact]
    public void ExpireTimeExactlyNow_IsExpired()
    {
        var snapshot = new CharacterInventorySnapshot([RentalRow(1, expireTime: 1000)]);

        var expired = RentalExpiryResolver.FindExpired(snapshot, nowUnixSeconds: 1000);

        Assert.Single(expired);
    }

    [Fact]
    public void ExpireTimeInTheFuture_IsNotExpired()
    {
        var snapshot = new CharacterInventorySnapshot([RentalRow(1, expireTime: 2000)]);

        Assert.Empty(RentalExpiryResolver.FindExpired(snapshot, nowUnixSeconds: 1000));
    }

    [Fact]
    public void MultipleRows_OnlyExpiredOnesAreReturned()
    {
        var snapshot = new CharacterInventorySnapshot([
            RentalRow(1, expireTime: 500),   // expired
            NonRentalRow(2),                  // not a rental
            RentalRow(3, expireTime: 2000),   // not yet due
            RentalRow(4, expireTime: 999),    // expired
        ]);

        var expired = RentalExpiryResolver.FindExpired(snapshot, nowUnixSeconds: 1000);

        Assert.Equal(2, expired.Count);
        Assert.Contains(expired, e => e.DurableId == 1);
        Assert.Contains(expired, e => e.DurableId == 4);
    }
}
