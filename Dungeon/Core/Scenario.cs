using Dungeon.Entities;

namespace Dungeon.Core;

// Everything needed to start an episode: static layout plus initial entity placement.
public sealed record Scenario(
    DungeonMap Map,
    IReadOnlyList<Door> Doors,
    IReadOnlyList<Guard> Guards,
    IReadOnlyList<Item> Items)
{
    public static Scenario Empty(DungeonMap map) =>
        new(map, Array.Empty<Door>(), Array.Empty<Guard>(), Array.Empty<Item>());
}
