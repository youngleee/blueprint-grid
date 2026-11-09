using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Tests;

public class FactSetTests
{
    [Fact]
    public void Facts_never_contain_unseen_entities_across_1000_seeds()
    {
        for (var seed = 1; seed <= 1000; seed++)
        {
            var scenario = ScenarioGenerator.Generate(seed, ScenarioOptions.Tier3);
            var a = new AgentContext(new DungeonWorld(scenario, seed));
            var facts = FactSet.From(a);
            var visibleIds = scenario.Doors.Where(d => a.World.IsVisible(d.Position)).Select(d => d.Id)
                .Concat(scenario.Guards.Where(g => a.World.IsVisible(g.Position)).Select(g => g.Id))
                .Concat(scenario.Items.Where(i => a.World.IsVisible(i.Position!.Value)).Select(i => i.Id))
                .ToHashSet();
            Assert.All(facts.Entities, e => Assert.Contains(e.Sighting.Id, visibleIds));
            if (!a.World.IsVisible(scenario.Map.Exit))
                Assert.Null(facts.Exit);
        }
    }

    [Fact]
    public void Text_lists_inventory_sight_memory_and_last_action()
    {
        var map = TestMaps.Corridor;
        var a = new AgentContext(TestMaps.World(map,
            doors: [new Door("door_1", new Cell(4, 1), DoorKind.Locked, 0, 0)],
            items: [TestMaps.AtStart(map, "key_1", ItemType.Key, opens: "door_1")]));
        a.Decide(c => c.Do(w => w.PickUp()));

        var text = FactSet.From(a).ToText();

        Assert.Contains("inventory: key_1 (key, opens door_1)", text);
        Assert.Contains("door_1 locked door at (4,1), closed, distance 3", text);
        Assert.Contains("exit not found yet", text);
        Assert.Contains("last action: pick_up -> success", text);
    }

    [Fact]
    public void Entities_are_ordered_in_sight_first_then_by_distance()
    {
        var map = TestMaps.Parse(
            "#########",
            "#S......#",
            "#.......#",
            "#......X#",
            "#########");
        var a = new AgentContext(TestMaps.World(map, items:
        [
            new Item("coin_1", ItemType.Coin, new Cell(4, 1)),
            new Item("food_1", ItemType.Food, new Cell(2, 1))
        ]));
        Assert.Equal(["food_1", "coin_1"], FactSet.From(a).Entities.Select(e => e.Sighting.Id));
    }

    [Fact]
    public void Facts_text_is_deterministic()
    {
        var s = ScenarioGenerator.Generate(7, ScenarioOptions.Tier3);
        var t1 = FactSet.From(new AgentContext(new DungeonWorld(s, 7))).ToText();
        var t2 = FactSet.From(new AgentContext(new DungeonWorld(s, 7))).ToText();
        Assert.Equal(t1, t2);
    }
}
