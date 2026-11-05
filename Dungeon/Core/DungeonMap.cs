namespace Dungeon.Core;

public enum Tile : byte { Wall, Floor }

// Static layout of one dungeon. Immutable after generation, so clones can share it.
public sealed class DungeonMap
{
    private readonly Tile[,] _tiles;

    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<Room> Rooms { get; }
    public Cell Start { get; }
    public Cell Exit { get; }

    public DungeonMap(Tile[,] tiles, IReadOnlyList<Room> rooms, Cell start, Cell exit)
    {
        _tiles = tiles;
        Width = tiles.GetLength(0);
        Height = tiles.GetLength(1);
        Rooms = rooms;
        Start = start;
        Exit = exit;
    }

    public bool InBounds(Cell c) => c.X >= 0 && c.X < Width && c.Y >= 0 && c.Y < Height;

    public Tile this[Cell c] => InBounds(c) ? _tiles[c.X, c.Y] : Tile.Wall;

    public bool IsFloor(Cell c) => this[c] == Tile.Floor;

    public int? RoomIndexOf(Cell c)
    {
        for (var i = 0; i < Rooms.Count; i++)
            if (Rooms[i].Contains(c))
                return i;
        return null;
    }

    public IEnumerable<Cell> Neighbours(Cell c)
    {
        foreach (var d in Enum.GetValues<Direction>())
        {
            var n = c.Step(d);
            if (IsFloor(n))
                yield return n;
        }
    }

    // Debug/visual dump: '#' wall, '.' floor, 'S' start, 'X' exit.
    public string Render()
    {
        var sb = new System.Text.StringBuilder();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var c = new Cell(x, y);
                sb.Append(c == Start ? 'S' : c == Exit ? 'X' : IsFloor(c) ? '.' : '#');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
