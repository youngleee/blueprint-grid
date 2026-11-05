using Dungeon.Core;

namespace Dungeon.Tests;

public class GeneratorTests
{
    private static readonly int[] Seeds = Enumerable.Range(1, 1000).ToArray();

    [Fact]
    public void Every_seed_produces_a_valid_connected_dungeon()
    {
        var o = new GeneratorOptions();
        foreach (var seed in Seeds)
        {
            var map = Generator.Generate(seed, o);

            Assert.InRange(map.Rooms.Count, o.MinRooms, o.MaxRooms);
            Assert.NotEqual(map.Start, map.Exit);
            Assert.True(map.IsFloor(map.Start), $"seed {seed}: start not floor");
            Assert.True(map.IsFloor(map.Exit), $"seed {seed}: exit not floor");
            Assert.Equal(0, map.RoomIndexOf(map.Start));
            Assert.Equal(map.Rooms.Count - 1, map.RoomIndexOf(map.Exit));

            for (var x = 0; x < map.Width; x++)
            {
                Assert.False(map.IsFloor(new Cell(x, 0)), $"seed {seed}: top border open");
                Assert.False(map.IsFloor(new Cell(x, map.Height - 1)), $"seed {seed}: bottom border open");
            }
            for (var y = 0; y < map.Height; y++)
            {
                Assert.False(map.IsFloor(new Cell(0, y)), $"seed {seed}: left border open");
                Assert.False(map.IsFloor(new Cell(map.Width - 1, y)), $"seed {seed}: right border open");
            }

            var reachable = FloodFill(map, map.Start);
            var floorCount = 0;
            for (var x = 0; x < map.Width; x++)
                for (var y = 0; y < map.Height; y++)
                    if (map.IsFloor(new Cell(x, y)))
                        floorCount++;
            Assert.True(reachable.Count == floorCount, $"seed {seed}: {floorCount - reachable.Count} floor cells unreachable");
        }
    }

    [Fact]
    public void Rooms_do_not_overlap()
    {
        foreach (var seed in Seeds)
        {
            var rooms = Generator.Generate(seed).Rooms;
            for (var i = 0; i < rooms.Count; i++)
                for (var j = i + 1; j < rooms.Count; j++)
                    Assert.False(rooms[i].Intersects(rooms[j], 0), $"seed {seed}: rooms {i},{j} overlap");
        }
    }

    [Fact]
    public void Same_seed_gives_identical_map()
    {
        foreach (var seed in Seeds.Take(50))
            Assert.Equal(Generator.Generate(seed).Render(), Generator.Generate(seed).Render());
    }

    [Fact]
    public void Different_seeds_give_different_maps()
    {
        var distinct = Seeds.Take(100).Select(s => Generator.Generate(s).Render()).Distinct().Count();
        Assert.Equal(100, distinct);
    }

    private static HashSet<Cell> FloodFill(DungeonMap map, Cell start)
    {
        var seen = new HashSet<Cell> { start };
        var queue = new Queue<Cell>();
        queue.Enqueue(start);
        while (queue.Count > 0)
            foreach (var n in map.Neighbours(queue.Dequeue()))
                if (seen.Add(n))
                    queue.Enqueue(n);
        return seen;
    }
}
