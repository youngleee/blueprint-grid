using Dungeon.Core;

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
}
