using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Tests;

public class AgentMemoryTests
{
    [Fact]
    public void Only_entities_in_sight_are_recorded()
    {
        var map = TestMaps.Corridor;
        var near = new Door("door_1", new Cell(3, 1), DoorKind.Jammed, 0, 1);
        var far = new Guard("guard_1", new Cell(8, 1), TestMaps.Traits(patience: 9));
        var a = new AgentContext(TestMaps.World(map, doors: [near], guards: [far], sightRadius: 3));

        Assert.NotNull(a.Memory.Get("door_1"));
        Assert.Null(a.Memory.Get("guard_1"));
        Assert.Equal(["door_1"], a.NewlySeen);
    }

    [Fact]
    public void Memory_keeps_last_known_state_when_entity_leaves_sight()
    {
        // Sight 2, guard detection 2. At (2,1) both see each other; back at (1,1) neither does.
        var guard = new Guard("guard_1", new Cell(4, 1), TestMaps.Traits(patience: 9));
        var a = new AgentContext(TestMaps.World(TestMaps.Corridor, guards: [guard], sightRadius: 2));
        Assert.Null(a.Memory.Get("guard_1"));

        a.Do(w => w.Move(Direction.E));
        Assert.Equal("alert", a.Memory.Get("guard_1")!.State);

        a.Do(w => w.Move(Direction.W));
        Assert.False(a.World.IsVisible(guard.Position));
        Assert.Equal(GuardState.Idle, a.World.Guards.Single().State);
        // The world changed but the agent did not see it: memory keeps the stale belief.
        Assert.Equal("alert", a.Memory.Get("guard_1")!.State);
        Assert.Equal(1, a.Memory.Get("guard_1")!.LastSeenTick);
    }

    [Fact]
    public void Picked_up_item_becomes_held_and_given_item_is_forgotten()
    {
        var map = TestMaps.Corridor;
        var guard = new Guard("guard_1", new Cell(3, 1), TestMaps.Traits(food: 1.0, patience: 9));
        var a = new AgentContext(TestMaps.World(map, guards: [guard],
            items: [new Item("food_1", ItemType.Food, new Cell(2, 1))]));

        a.Do(w => w.Move(Direction.E));
        a.Do(w => w.PickUp());
        Assert.Equal("held", a.Memory.Get("food_1")!.State);

        a.Do(w => w.Give("food_1", "guard_1"));
        Assert.Null(a.Memory.Get("food_1"));
    }

    [Fact]
    public void Thrown_item_is_remembered_on_its_new_cell()
    {
        var map = TestMaps.Corridor;
        var a = new AgentContext(TestMaps.World(map, items: [TestMaps.AtStart(map, "food_1", ItemType.Food)]));
        a.Do(w => w.PickUp());
        a.Do(w => w.Throw("food_1", new Cell(3, 1)));
        var s = a.Memory.Get("food_1")!;
        Assert.Equal("on_floor", s.State);
        Assert.Equal(new Cell(3, 1), s.Position);
    }

    [Fact]
    public void Exit_position_is_learned_only_when_seen()
    {
        var a = new AgentContext(TestMaps.World(TestMaps.Corridor, sightRadius: 2));
        Assert.Null(a.Memory.ExitPosition);
        for (var i = 0; i < 6; i++)
            a.Do(w => w.Move(Direction.E));
        Assert.Equal(TestMaps.Corridor.Exit, a.Memory.ExitPosition);
    }

    [Fact]
    public void Clone_copies_memory_independently()
    {
        var map = TestMaps.Corridor;
        var a = new AgentContext(TestMaps.World(map, items: [TestMaps.AtStart(map, "food_1", ItemType.Food)]));
        var b = a.Clone();
        b.Do(w => w.PickUp());
        Assert.Equal("on_floor", a.Memory.Get("food_1")!.State);
        Assert.Equal("held", b.Memory.Get("food_1")!.State);
    }
}
