using Dungeon.Entities;

namespace Dungeon.Core;

public sealed record ScenarioOptions(
    int Guards = 1,
    int LockedDoors = 1,
    int JammedDoors = 0,
    int ExtraItems = 1,
    int MinObstacleSpacing = 4,
    // Per-entity trait ranges (PLAN.md §5.4). Drawn per entity so held-out seeds behave differently.
    double CalmMin = 0.3, double CalmMax = 0.7,
    double FoodMin = 0.5, double FoodMax = 0.9,
    double CoinMin = 0.2, double CoinMax = 0.8,
    double ThrowMin = 0.6, double ThrowMax = 1.0,
    double PickMin = 0.2, double PickMax = 0.5,
    double PushMin = 0.3, double PushMax = 0.6,
    int DistractMinTicks = 3, int DistractMaxTicks = 6,
    int MinPatience = 3, int MaxPatience = 5)
{
    public static ScenarioOptions Tier1 => new(Guards: 1, LockedDoors: 0, JammedDoors: 0);
    public static ScenarioOptions Tier2 => new(Guards: 0, LockedDoors: 1, JammedDoors: 1);
    public static ScenarioOptions Tier3 => new(Guards: 1, LockedDoors: 1, JammedDoors: 1);
}

// Places obstacles on chokepoints between start and exit, and the items needed for them
// in the region reachable before each obstacle, so every scenario is solvable.
public static class ScenarioGenerator
{
    public static Scenario Generate(int seed, ScenarioOptions? options = null, GeneratorOptions? layout = null)
    {
        var o = options ?? new ScenarioOptions();
        var map = Generator.Generate(seed, layout);
        var rng = Rng.ForStream(seed, "entity");

        var obstacleCount = o.Guards + o.LockedDoors + o.JammedDoors;
        var cuts = ChokepointsInOrder(map);
        var chosen = new List<Cell>();
        // Short chains can lack spaced chokepoints; relax spacing before giving up, so the
        // seed stays usable and the layout stays identical to the plain map for that seed.
        for (var spacing = o.MinObstacleSpacing; spacing >= 1 && chosen.Count < obstacleCount; spacing--)
            chosen = PickSpaced(cuts, obstacleCount, spacing, rng.Clone());
        if (chosen.Count < obstacleCount)
            throw new InvalidOperationException($"Seed {seed}: only {chosen.Count}/{obstacleCount} chokepoints available");
        rng.NextULong();

        var kinds = Enumerable.Repeat("guard", o.Guards)
            .Concat(Enumerable.Repeat("locked", o.LockedDoors))
            .Concat(Enumerable.Repeat("jammed", o.JammedDoors))
            .ToList();
        Shuffle(kinds, rng);

        var doors = new List<Door>();
        var guards = new List<Guard>();
        var items = new List<Item>();
        var occupied = new HashSet<Cell>(chosen) { map.Start, map.Exit };

        for (var i = 0; i < chosen.Count; i++)
        {
            var cell = chosen[i];
            // Items for obstacle i must be reachable with obstacles i..n still closed.
            var region = Reachable(map, map.Start, chosen.Skip(i).ToHashSet());
            switch (kinds[i])
            {
                case "guard":
                    var guard = new Guard($"guard_{guards.Count + 1}", cell, new GuardTraits(
                        rng.Range(o.CalmMin, o.CalmMax),
                        rng.Range(o.FoodMin, o.FoodMax),
                        rng.Range(o.CoinMin, o.CoinMax),
                        rng.Range(o.ThrowMin, o.ThrowMax),
                        o.DistractMinTicks,
                        o.DistractMaxTicks,
                        rng.Next(o.MinPatience, o.MaxPatience + 1)));
                    guards.Add(guard);
                    items.Add(PlaceItem(map, region, occupied, rng, $"food_{items.Count + 1}", ItemType.Food));
                    break;
                case "locked":
                    var locked = new Door($"door_{doors.Count + 1}", cell, DoorKind.Locked,
                        rng.Range(o.PickMin, o.PickMax), 0);
                    doors.Add(locked);
                    items.Add(PlaceItem(map, region, occupied, rng, $"key_{items.Count + 1}", ItemType.Key, locked.Id));
                    break;
                case "jammed":
                    doors.Add(new Door($"door_{doors.Count + 1}", cell, DoorKind.Jammed,
                        0, rng.Range(o.PushMin, o.PushMax)));
                    break;
            }
        }

        // Optional items give the agent alternatives (e.g. coin bribe, lockpick) and distractors.
        var firstRegion = Reachable(map, map.Start, chosen.ToHashSet());
        var extraTypes = new[] { ItemType.Coin, ItemType.Lockpick, ItemType.Food, ItemType.Tool };
        for (var i = 0; i < o.ExtraItems; i++)
        {
            var type = extraTypes[rng.Next(0, extraTypes.Length)];
            items.Add(PlaceItem(map, firstRegion, occupied, rng, $"{type.ToString().ToLowerInvariant()}_{items.Count + 1}", type));
        }

        return new Scenario(map, doors, guards, items);
    }

    // Floor cells outside rooms whose removal disconnects start from exit, ordered by
    // distance from start. Corridor-only keeps obstacles in doorways, not mid-room.
    public static List<Cell> ChokepointsInOrder(DungeonMap map)
    {
        var distance = Distances(map, map.Start, new HashSet<Cell>());
        var result = new List<Cell>();
        foreach (var (cell, _) in distance.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Y))
        {
            if (cell == map.Start || cell == map.Exit || map.RoomIndexOf(cell) is not null)
                continue;
            if (!Reachable(map, map.Start, new HashSet<Cell> { cell }).Contains(map.Exit))
                result.Add(cell);
        }
        return result;
    }

    public static HashSet<Cell> Reachable(DungeonMap map, Cell from, IReadOnlySet<Cell> blocked) =>
        Distances(map, from, blocked).Keys.ToHashSet();

    private static Dictionary<Cell, int> Distances(DungeonMap map, Cell from, IReadOnlySet<Cell> blocked)
    {
        var dist = new Dictionary<Cell, int> { [from] = 0 };
        var queue = new Queue<Cell>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var n in map.Neighbours(c))
                if (!blocked.Contains(n) && dist.TryAdd(n, dist[c] + 1))
                    queue.Enqueue(n);
        }
        return dist;
    }

    // Splits the ordered cuts into `count` segments and picks one random cell per segment,
    // so obstacles spread along the chain instead of clustering in one corridor.
    private static List<Cell> PickSpaced(List<Cell> cuts, int count, int spacing, Rng rng)
    {
        var picked = new List<Cell>();
        if (count == 0)
            return picked;
        for (var s = 0; s < count; s++)
        {
            var from = s * cuts.Count / count;
            var to = (s + 1) * cuts.Count / count;
            var options = cuts.Skip(from).Take(to - from)
                .Where(c => picked.All(p => p.Manhattan(c) >= spacing))
                .ToList();
            if (options.Count > 0)
                picked.Add(options[rng.Next(0, options.Count)]);
        }
        return picked;
    }

    private static Item PlaceItem(DungeonMap map, HashSet<Cell> region, HashSet<Cell> occupied, Rng rng,
        string id, ItemType type, string? opens = null)
    {
        var candidates = region
            .Where(c => map.RoomIndexOf(c) is not null && !occupied.Contains(c))
            .OrderBy(c => c.X).ThenBy(c => c.Y)
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException($"No free cell for {id}");
        var cell = candidates[rng.Next(0, candidates.Count)];
        occupied.Add(cell);
        return new Item(id, type, cell, opens);
    }

    private static void Shuffle<T>(IList<T> list, Rng rng)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
