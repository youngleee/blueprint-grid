using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Tests;

// Omniscient test-only solver for door scenarios. Not an agent baseline: it reads the full map.
internal static class ScriptedSolver
{
    public static void Run(DungeonWorld world, int maxTicks)
    {
        while (world.Status == EpisodeStatus.Running && world.Tick < maxTicks)
        {
            var item = world.Items.FirstOrDefault(i => !i.IsHeld && Reachable(world, i.Position!.Value));
            if (item is not null)
            {
                if (world.AgentPosition == item.Position)
                    world.PickUp();
                else
                    StepToward(world, item.Position!.Value);
                continue;
            }

            if (Reachable(world, world.Map.Exit))
            {
                StepToward(world, world.Map.Exit);
                continue;
            }

            var door = world.Doors.Where(d => !d.IsOpen).OrderBy(d => PathLength(world, d.Position)).First();
            if (door.Position.Manhattan(world.AgentPosition) != 1)
            {
                StepToward(world, door.Position);
                continue;
            }

            var key = world.Inventory.FirstOrDefault(i => i.Opens == door.Id);
            world.Use(door.Kind == DoorKind.Locked ? key?.Id : null, door.Id);
        }
    }

    private static bool Reachable(DungeonWorld world, Cell target) => PathLength(world, target) < int.MaxValue;

    // BFS distance where the target itself may be impassable (to walk next to a door).
    private static int PathLength(DungeonWorld world, Cell target) =>
        FirstStep(world, target) is { } step ? step.length : int.MaxValue;

    private static void StepToward(DungeonWorld world, Cell target)
    {
        var step = FirstStep(world, target) ?? throw new InvalidOperationException($"no path to {target}");
        world.Move(step.direction);
    }

    private static (Direction direction, int length)? FirstStep(DungeonWorld world, Cell target)
    {
        var start = world.AgentPosition;
        if (start == target)
            return (Direction.N, 0);
        var first = new Dictionary<Cell, (Direction dir, int len)>();
        var queue = new Queue<Cell>();
        foreach (var d in Enum.GetValues<Direction>())
        {
            var n = start.Step(d);
            if (n == target || world.IsPassable(n))
            {
                if (n == target)
                    return (d, 1);
                first[n] = (d, 1);
                queue.Enqueue(n);
            }
        }
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var d in Enum.GetValues<Direction>())
            {
                var n = c.Step(d);
                if (first.ContainsKey(n) || n == start)
                    continue;
                if (n == target)
                    return (first[c].dir, first[c].len + 1);
                if (!world.IsPassable(n))
                    continue;
                first[n] = (first[c].dir, first[c].len + 1);
                queue.Enqueue(n);
            }
        }
        return null;
    }
}
