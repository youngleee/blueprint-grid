using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Tests;

// Builds small hand-drawn maps so expectations can be read off the picture.
// Legend: '#' wall, '.' floor, 'S' start (floor), 'X' exit (floor).
internal static class TestMaps
{
    public static DungeonMap Parse(params string[] rows)
    {
        var height = rows.Length;
        var width = rows[0].Length;
        var tiles = new Tile[width, height];
        Cell start = default, exit = default;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var ch = rows[y][x];
                tiles[x, y] = ch == '#' ? Tile.Wall : Tile.Floor;
                if (ch == 'S') start = new Cell(x, y);
                if (ch == 'X') exit = new Cell(x, y);
            }
        return new DungeonMap(tiles, new[] { new Room(0, 0, width, height) }, start, exit);
    }

    // Corridor "#S.......X#": agent at (1,1), exit at (9,1).
    public static DungeonMap Corridor => Parse(
        "###########",
        "#S.......X#",
        "###########");

    // Traits where every interaction succeeds or fails deterministically.
    public static GuardTraits Traits(double calm = 0, double food = 0, double coin = 0, double toss = 0,
        int distractTicks = 3, int patience = 3) =>
        new(calm, food, coin, toss, distractTicks, distractTicks, patience);

    public static DungeonWorld World(DungeonMap map, IEnumerable<Door>? doors = null, IEnumerable<Guard>? guards = null,
        IEnumerable<Item>? items = null, int sightRadius = 3, int seed = 1) =>
        new(new Scenario(map, (doors ?? []).ToList(), (guards ?? []).ToList(), (items ?? []).ToList()), seed, sightRadius);

    // An item lying on the start cell, so a single pick_up puts it in the inventory.
    public static Item AtStart(DungeonMap map, string id, ItemType type, string? opens = null) =>
        new(id, type, map.Start, opens);
}
