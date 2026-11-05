namespace Dungeon.Core;

public static class Visibility
{
    // Cells within Chebyshev radius whose line of sight is not blocked.
    // Opaque cells themselves are visible (you see the wall or door), but not what lies behind them.
    public static HashSet<Cell> VisibleFrom(DungeonMap map, Cell origin, int radius, Func<Cell, bool>? isOpaque = null)
    {
        isOpaque ??= c => !map.IsFloor(c);
        var visible = new HashSet<Cell>();
        for (var x = origin.X - radius; x <= origin.X + radius; x++)
            for (var y = origin.Y - radius; y <= origin.Y + radius; y++)
            {
                var target = new Cell(x, y);
                if (map.InBounds(target) && HasLineOfSight(origin, target, isOpaque))
                    visible.Add(target);
            }
        return visible;
    }

    public static bool HasLineOfSight(DungeonMap map, Cell origin, Cell target) =>
        HasLineOfSight(origin, target, c => !map.IsFloor(c));

    // Bresenham line; blocked if any cell strictly between origin and target is opaque.
    public static bool HasLineOfSight(Cell origin, Cell target, Func<Cell, bool> isOpaque)
    {
        int x0 = origin.X, y0 = origin.Y, x1 = target.X, y1 = target.Y;
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;
        while (true)
        {
            if (x0 == x1 && y0 == y1)
                return true;
            var current = new Cell(x0, y0);
            if (current != origin && isOpaque(current))
                return false;
            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
