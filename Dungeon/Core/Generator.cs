namespace Dungeon.Core;

public sealed record GeneratorOptions(
    int Width = 40,
    int Height = 40,
    int MinRooms = 4,
    int MaxRooms = 6,
    int MinRoomSize = 5,
    int MaxRoomSize = 10,
    int RoomMargin = 2,
    int MaxPlacementAttempts = 500);

// Seeded room-and-corridor generator. Entities are placed in a later step (week 3).
public static class Generator
{
    public static DungeonMap Generate(int seed, GeneratorOptions? options = null)
    {
        var o = options ?? new GeneratorOptions();
        var rng = Rng.ForStream(seed, "layout");
        var tiles = new Tile[o.Width, o.Height];
        var target = rng.Next(o.MinRooms, o.MaxRooms + 1);
        var rooms = new List<Room>();

        for (var attempt = 0; attempt < o.MaxPlacementAttempts && rooms.Count < target; attempt++)
        {
            var w = rng.Next(o.MinRoomSize, o.MaxRoomSize + 1);
            var h = rng.Next(o.MinRoomSize, o.MaxRoomSize + 1);
            // Keep a 1-cell wall border around the whole map.
            var room = new Room(rng.Next(1, o.Width - w - 1), rng.Next(1, o.Height - h - 1), w, h);
            if (rooms.All(r => !r.Intersects(room, o.RoomMargin)))
                rooms.Add(room);
        }

        if (rooms.Count < o.MinRooms)
            throw new InvalidOperationException($"Seed {seed}: placed only {rooms.Count} rooms");

        // Order rooms along a chain so the exit is "deep" and obstacles placed on the
        // chain later are guaranteed to lie between start and exit.
        rooms = OrderAsChain(rooms);

        foreach (var room in rooms)
            for (var x = room.X; x < room.X + room.Width; x++)
                for (var y = room.Y; y < room.Y + room.Height; y++)
                    tiles[x, y] = Tile.Floor;

        for (var i = 0; i < rooms.Count - 1; i++)
            CarveCorridor(tiles, rooms[i].Center, rooms[i + 1].Center, rng.Chance(0.5));

        return new DungeonMap(tiles, rooms, rooms[0].Center, rooms[^1].Center);
    }

    // Greedy nearest-neighbour chain starting from the room closest to the top-left corner.
    private static List<Room> OrderAsChain(List<Room> rooms)
    {
        var remaining = new List<Room>(rooms);
        var current = remaining.OrderBy(r => r.X + r.Y).First();
        var ordered = new List<Room> { current };
        remaining.Remove(current);
        while (remaining.Count > 0)
        {
            var next = remaining.OrderBy(r => r.Center.Manhattan(current.Center)).First();
            ordered.Add(next);
            remaining.Remove(next);
            current = next;
        }
        return ordered;
    }

    private static void CarveCorridor(Tile[,] tiles, Cell from, Cell to, bool horizontalFirst)
    {
        var corner = horizontalFirst ? new Cell(to.X, from.Y) : new Cell(from.X, to.Y);
        CarveLine(tiles, from, corner);
        CarveLine(tiles, corner, to);
    }

    private static void CarveLine(Tile[,] tiles, Cell a, Cell b)
    {
        var dx = Math.Sign(b.X - a.X);
        var dy = Math.Sign(b.Y - a.Y);
        var c = a;
        tiles[c.X, c.Y] = Tile.Floor;
        while (c != b)
        {
            c = new Cell(c.X + dx, c.Y + dy);
            tiles[c.X, c.Y] = Tile.Floor;
        }
    }
}
