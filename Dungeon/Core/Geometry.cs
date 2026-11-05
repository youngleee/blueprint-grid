namespace Dungeon.Core;

public readonly record struct Cell(int X, int Y)
{
    public Cell Step(Direction direction) => direction switch
    {
        Direction.N => this with { Y = Y - 1 },
        Direction.S => this with { Y = Y + 1 },
        Direction.E => this with { X = X + 1 },
        Direction.W => this with { X = X - 1 },
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    public int Chebyshev(Cell other) => Math.Max(Math.Abs(X - other.X), Math.Abs(Y - other.Y));

    public int Manhattan(Cell other) => Math.Abs(X - other.X) + Math.Abs(Y - other.Y);

    public override string ToString() => $"({X},{Y})";
}

public enum Direction { N, E, S, W }

public readonly record struct Room(int X, int Y, int Width, int Height)
{
    public Cell Center => new(X + Width / 2, Y + Height / 2);

    public bool Contains(Cell cell) =>
        cell.X >= X && cell.X < X + Width && cell.Y >= Y && cell.Y < Y + Height;

    public bool Intersects(Room other, int margin) =>
        X - margin < other.X + other.Width && other.X - margin < X + Width &&
        Y - margin < other.Y + other.Height && other.Y - margin < Y + Height;
}
