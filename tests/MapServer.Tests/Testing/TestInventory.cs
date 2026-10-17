using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.Items;
using Athena.Net.MapServer.Tests.Gameplay.Rules;

namespace MapServer.Tests.Testing;

internal static class TestInventory
{
    /// <summary>A session inventory service over the given CharServer double: the deployment's rules, the real item catalog, no retry delay.</summary>
    internal static CharacterInventoryService Service(
        uint accountId, uint charId, ICharacterInventoryOperations operations, CharacterInventorySnapshot snapshot,
        Func<InventoryLimits>? limits = null, GameplayRegion region = GameplayRegion.Iro) =>
        new(accountId, charId, operations, snapshot, TestGameplayRules.Services(region).Inventory, ItemCatalog.Current.ById, limits, retryDelay: TimeSpan.Zero);
}
