using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Tests;

public class NavigationTests
{
    private static readonly DungeonMap Room = TestMaps.Parse(
        "#########",
        "#S......#",
        "#.......#",
        "#......X#",
        "#########");

    [Fact]
    public void GoTo_reaches_known_cell_by_shortest_path()
    {
        var a = new AgentContext(TestMaps.World(Room, sightRadius: 9));
        var r = Navigation.GoTo(a, new Cell(4, 3), 0, "cell");
        Assert.Equal(HelperStatus.Arrived, r.Status);
        Assert.Equal(new Cell(4, 3), a.Position);
        Assert.Equal(5, r.Ticks);
    }

    [Fact]
    public void GoTo_entity_stops_adjacent_to_doors_and_guards()
    {
        var map = TestMaps.Corridor;
        var a = new AgentContext(TestMaps.World(map,
            doors: [new Door("door_1", new Cell(4, 1), DoorKind.Locked, 0, 0)]));
        var r = Navigation.GoTo(a, "door_1");
        Assert.Equal(HelperStatus.Arrived, r.Status);
        Assert.Equal(new Cell(3, 1), a.Position);
    }

    [Fact]
    public void GoTo_item_stands_on_it()
    {
        var a = new AgentContext(TestMaps.World(Room, items: [new Item("coin_1", ItemType.Coin, new Cell(3, 2))]));
        Assert.Equal(HelperStatus.Arrived, Navigation.GoTo(a, "coin_1").Status);
        Assert.Equal(new Cell(3, 2), a.Position);
    }

    [Fact]
    public void GoTo_unknown_entity_is_unreachable_and_free()
    {
        var a = new AgentContext(TestMaps.World(Room));
        var r = Navigation.GoTo(a, "guard_7");
        Assert.Equal(HelperStatus.Unreachable, r.Status);
        Assert.Equal(0, a.Tick);
    }

    [Fact]
    public void GoTo_does_not_plan_through_unknown_cells()
    {
        var a = new AgentContext(TestMaps.World(TestMaps.Corridor, sightRadius: 1));
        var r = Navigation.GoTo(a, TestMaps.Corridor.Exit, 0, "exit");
        Assert.Equal(HelperStatus.Unreachable, r.Status);
        Assert.Equal(0, a.Tick);
    }

    [Fact]
    public void GoTo_does_not_plan_through_remembered_closed_door()
    {
        var map = TestMaps.Corridor;
        var a = new AgentContext(TestMaps.World(map,
            doors: [new Door("door_1", new Cell(3, 1), DoorKind.Locked, 0, 0)], sightRadius: 9));
        Assert.Equal(HelperStatus.Unreachable, Navigation.GoTo(a, new Cell(5, 1), 0, "cell").Status);
    }

    [Fact]
    public void GoTo_is_interrupted_when_a_new_entity_comes_into_sight()
    {
        var map = TestMaps.Parse(
            "############",
            "#S.........#",
            "#.........X#",
            "############");
        var guard = new Guard("guard_1", new Cell(9, 1), TestMaps.Traits(patience: 9));
        var a = new AgentContext(TestMaps.World(map, guards: [guard], sightRadius: 3));
        // Plan toward a cell already known; the guard enters sight on the way.
        var r = Navigation.GoTo(a, new Cell(4, 1), 0, "cell");
        Assert.Equal(HelperStatus.Arrived, r.Status);
        var r2 = Navigation.GoTo(a, new Cell(7, 1), 0, "cell");
        Assert.Equal(HelperStatus.Interrupted, r2.Status);
        Assert.Contains("spotted guard_1", r2.Message);
    }

    [Fact]
    public void GoTo_respects_tick_limit()
    {
        var a = new AgentContext(TestMaps.World(Room, sightRadius: 9));
        var r = Navigation.GoTo(a, new Cell(7, 3), 0, "cell", maxTicks: 2);
        Assert.Equal(HelperStatus.Interrupted, r.Status);
        Assert.Equal(2, r.Ticks);
    }

    [Fact]
    public void Explore_moves_toward_unknown_and_stops_on_discovery()
    {
        var map = TestMaps.Corridor;
        var a = new AgentContext(TestMaps.World(map,
            items: [new Item("coin_1", ItemType.Coin, new Cell(6, 1))], sightRadius: 1));
        var r = Navigation.Explore(a);
        Assert.Equal(HelperStatus.Interrupted, r.Status);
        Assert.Contains("spotted coin_1", r.Message);
        Assert.Equal(new Cell(5, 1), a.Position);
    }

    [Fact]
    public void Explore_reports_when_nothing_is_left()
    {
        var a = new AgentContext(TestMaps.World(Room, sightRadius: 9));
        Assert.Equal(HelperStatus.Unreachable, Navigation.Explore(a).Status);
    }

    [Fact]
    public void Helper_outcome_becomes_last_action_in_facts()
    {
        var a = new AgentContext(TestMaps.World(Room, sightRadius: 9));
        a.Decide(c => Navigation.GoTo(c, new Cell(3, 1), 0, "cell"));
        Assert.Contains("last action: go_to(cell) -> arrived after 2 ticks", FactSet.From(a).ToText());
    }

    // End-to-end: helpers + primitives clear every tier-2 seed with only agent knowledge.
    // Strategy: explore; pick up seen items; open doors (key if held, push if jammed); go to exit.
    [Fact]
    public void Knowledge_only_policy_clears_every_tier2_seed()
    {
        var failures = new List<string>();
        for (var seed = 1; seed <= 200; seed++)
        {
            var a = new AgentContext(new DungeonWorld(ScenarioGenerator.Generate(seed, ScenarioOptions.Tier2), seed));
            for (var decisions = 0; decisions < 300 && !a.IsOver; decisions++)
                DoorPolicy(a);
            if (a.World.Status != EpisodeStatus.Succeeded)
                failures.Add($"seed {seed}: {a.World.Status} tick {a.Tick}");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures.Take(5)));
    }

    private static void DoorPolicy(AgentContext a)
    {
        if (a.Memory.ExitPosition is { } exit
            && Navigation.GoTo(a, exit, 0, "exit").Status == HelperStatus.Arrived)
            return;

        var item = a.Memory.Sightings.Values
            .Where(s => s is { Kind: EntityKind.Item, State: "on_floor" })
            .FirstOrDefault(s => s.Position == a.Position || Navigation.PathDistance(a, [s.Position!.Value]) is not null);
        if (item is not null)
        {
            if (a.Position == item.Position)
                a.Do(w => w.PickUp());
            else
                Navigation.GoTo(a, item.Id);
            return;
        }

        if (Navigation.Explore(a).Status != HelperStatus.Unreachable)
            return;

        // Closed doors: walk next to the nearest one whose neighbourhood is reachable, then try to open it.
        var door = a.Memory.Sightings.Values
            .Where(s => s is { Kind: EntityKind.Door, State: "closed" })
            .OrderBy(s => s.Position!.Value.Manhattan(a.Position))
            .FirstOrDefault(s => s.Position!.Value.Manhattan(a.Position) == 1
                || Navigation.GoTo(a, s.Id).Status == HelperStatus.Arrived);
        if (door is null)
            return;
        var key = a.Memory.Sightings.Values.FirstOrDefault(s => s.State == "held" && s.Opens == door.Id);
        a.Do(w => w.Use(door.Subtype == "locked" ? key?.Id : null, door.Id));
    }
}
