using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Render;

// Text view of a world for the CLI and logs.
// Agent view: entities are drawn only on currently visible cells; remembered cells show terrain only,
// so the picture never leaks state the agent could not know.
public static class AsciiRenderer
{
    public const string Legend =
        "@ agent  X exit  # wall  . visible floor  , remembered floor  (blank) unknown\n" +
        "G guard (g = calm/bribed/distracted)  L locked door  J jammed door  / open door\n" +
        "k key  p lockpick  f food  c coin  t tool";

    // Crops to the bounding box of known floor (plus a 1-cell wall margin) so output stays small.
    public static string Render(DungeonWorld world, bool reveal = false)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (var y = 0; y < world.Map.Height; y++)
            for (var x = 0; x < world.Map.Width; x++)
            {
                var c = new Cell(x, y);
                if (!world.Map.IsFloor(c) || !(reveal || world.IsKnown(c)))
                    continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        if (minX == int.MaxValue)
            return "";

        var sb = new System.Text.StringBuilder();
        sb.Append($"   x={minX - 1}..{maxX + 1}\n");
        for (var y = minY - 1; y <= maxY + 1; y++)
        {
            sb.Append($"{y,2} ");
            for (var x = minX - 1; x <= maxX + 1; x++)
                sb.Append(Glyph(world, new Cell(x, y), reveal));
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    public static char Glyph(DungeonWorld world, Cell c, bool reveal)
    {
        var visible = reveal || world.IsVisible(c);
        var known = reveal || world.IsKnown(c);
        if (!known)
            return ' ';
        if (c == world.AgentPosition)
            return '@';
        if (!world.Map.IsFloor(c))
            return '#';
        if (visible)
        {
            if (world.GuardAt(c) is { } guard)
                return guard.BlocksMovement ? 'G' : 'g';
            if (world.DoorAt(c) is { } door)
                return door.IsOpen ? '/' : door.Kind == DoorKind.Locked ? 'L' : 'J';
            if (world.Items.FirstOrDefault(i => i.Position == c) is { } item)
                return ItemGlyph(item.Type);
        }
        if (c == world.Map.Exit)
            return 'X';
        return visible ? '.' : ',';
    }

    public static char ItemGlyph(ItemType type) => type switch
    {
        ItemType.Key => 'k',
        ItemType.Lockpick => 'p',
        ItemType.Food => 'f',
        ItemType.Coin => 'c',
        ItemType.Tool => 't',
        _ => '?'
    };

    // Agent-visible status lines: what is in sight right now, plus inventory.
    public static string Status(DungeonWorld world)
    {
        var lines = new List<string>
        {
            $"tick {world.Tick}  pos {world.AgentPosition}  status {world.Status}" +
                (world.FailureReason is null ? "" : $" ({world.FailureReason})"),
            $"inventory: {(world.Inventory.Any() ? string.Join(", ", world.Inventory) : "-")}"
        };
        foreach (var g in world.Guards.Where(g => world.IsVisible(g.Position)))
            lines.Add($"  {g.Id} at {g.Position}: {g.State.ToString().ToLowerInvariant()}");
        foreach (var d in world.Doors.Where(d => world.IsVisible(d.Position)))
            lines.Add($"  {d.Id} at {d.Position}: {d.Kind.ToString().ToLowerInvariant()}, {(d.IsOpen ? "open" : "closed")}");
        foreach (var i in world.Items.Where(i => !i.IsHeld && world.IsVisible(i.Position!.Value)))
            lines.Add($"  {i.Id} at {i.Position}");
        return string.Join('\n', lines);
    }

    // Debug only: hidden parameters of every entity (chances, patience).
    public static string Debug(DungeonWorld world)
    {
        var lines = new List<string>();
        foreach (var g in world.Guards)
            lines.Add($"{g.Id} {g.Position} {g.State.ToString().ToLowerInvariant()} patience={g.Patience}/{g.Traits.MaxPatience} " +
                $"distracted={g.DistractedRemaining} calm={g.Traits.CalmChance:F2} food={g.Traits.FoodChance:F2} " +
                $"coin={g.Traits.CoinChance:F2} throw={g.Traits.ThrowChance:F2} k={g.Traits.DistractMinTicks}-{g.Traits.DistractMaxTicks}");
        foreach (var d in world.Doors)
            lines.Add($"{d.Id} {d.Position} {d.Kind.ToString().ToLowerInvariant()} {(d.IsOpen ? "open" : "closed")} " +
                $"pick={d.PickChance:F2} push={d.PushChance:F2}");
        foreach (var i in world.Items)
            lines.Add($"{i.Id} {(i.IsHeld ? "held" : i.Position.ToString())}{(i.Opens is null ? "" : $" opens {i.Opens}")}");
        return string.Join('\n', lines);
    }
}
