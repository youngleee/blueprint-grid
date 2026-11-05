namespace Dungeon.Core;

public static class Visibility
{
    // Cells within Chebyshev radius whose line of sight is not blocked by a wall.
    // Wall cells themselves are visible (you see the wall), but not what lies behind them.
    public static HashSet<Cell> VisibleFrom(DungeonMap map, Cell origin, int radius)
    {
        var visible = new HashSet<Cell>();
        for (var x = origin.X - radius; x <= origin.X + radius; x++)
            for (var y = origin.Y - radius; y <= origin.Y + radius; y++)
            {
                var target = new Cell(x, y);
                if (map.InBounds(target) && HasLineOfSight(map, origin, target))
                    visible.Add(target);
            }
        return visible;
    }

    // Bresenham line; blocked if any cell strictly between origin and target is a wall.
    public static bool HasLineOfSight(DungeonMap map, Cell origin, Cell target)
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
            if (current != origin && !map.IsFloor(current))
                return false;
            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
