using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Tests;

public class DoorTests
{
    private static readonly DungeonMap Map = TestMaps.Corridor;
    private static readonly Cell DoorCell = new(2, 1);

    private static Door Locked(double pick = 0) => new("door_1", DoorCell, DoorKind.Locked, pick, 0);
    private static Door Jammed(double push) => new("door_1", DoorCell, DoorKind.Jammed, 0, push);

    [Fact]
    public void Closed_door_blocks_movement()
    {
        var world = TestMaps.World(Map, doors: [Locked()]);
        var result = world.Move(Direction.E);
        Assert.Equal(ActionStatus.Failed, result.Status);
        Assert.Contains("closed", result.Message);
        Assert.Equal(Map.Start, world.AgentPosition);
    }

    [Fact]
    public void Closed_door_blocks_sight_but_is_visible()
    {
        var world = TestMaps.World(Map, doors: [Locked()], sightRadius: 5);
        Assert.True(world.IsVisible(DoorCell));
        Assert.False(world.IsVisible(new Cell(3, 1)));
    }

    [Fact]
    public void Matching_key_always_opens_locked_door()
    {
        var world = TestMaps.World(Map, doors: [Locked()],
            items: [TestMaps.AtStart(Map, "key_1", ItemType.Key, opens: "door_1")]);
        world.PickUp();
        Assert.Equal(ActionStatus.Success, world.Use("key_1", "door_1").Status);
        Assert.True(world.Doors.Single().IsOpen);
        Assert.Equal(ActionStatus.Success, world.Move(Direction.E).Status);
    }

    [Fact]
    public void Wrong_key_fails_and_costs_a_tick()
    {
        var world = TestMaps.World(Map, doors: [Locked()],
            items: [TestMaps.AtStart(Map, "key_9", ItemType.Key, opens: "door_9")]);
        world.PickUp();
        var result = world.Use("key_9", "door_1");
        Assert.Equal(ActionStatus.Failed, result.Status);
        Assert.False(world.Doors.Single().IsOpen);
        Assert.Equal(2, world.Tick);
    }

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(0.0, false)]
    public void Lockpick_follows_pick_chance(double chance, bool opens)
    {
        var world = TestMaps.World(Map, doors: [Locked(chance)],
            items: [TestMaps.AtStart(Map, "lockpick_1", ItemType.Lockpick)]);
        world.PickUp();
        world.Use("lockpick_1", "door_1");
        Assert.Equal(opens, world.Doors.Single().IsOpen);
    }

    [Fact]
    public void Lockpick_success_rate_matches_chance_over_many_seeds()
    {
        var opened = 0;
        const int runs = 2000;
        for (var seed = 0; seed < runs; seed++)
        {
            var world = TestMaps.World(Map, doors: [Locked(0.3)],
                items: [TestMaps.AtStart(Map, "lockpick_1", ItemType.Lockpick)], seed: seed);
            world.PickUp();
            world.Use("lockpick_1", "door_1");
            if (world.Doors.Single().IsOpen)
                opened++;
        }
        Assert.InRange(opened / (double)runs, 0.26, 0.34);
    }

    [Fact]
    public void Locked_door_cannot_be_pushed()
    {
        var world = TestMaps.World(Map, doors: [Locked(1.0)]);
        Assert.Equal(ActionStatus.Failed, world.Use(null, "door_1").Status);
        Assert.False(world.Doors.Single().IsOpen);
    }

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(0.0, false)]
    public void Jammed_door_opens_by_pushing_with_push_chance(double chance, bool opens)
    {
        var world = TestMaps.World(Map, doors: [Jammed(chance)]);
        world.Use(null, "door_1");
        Assert.Equal(opens, world.Doors.Single().IsOpen);
    }

    [Fact]
    public void Use_requires_adjacency()
    {
        var far = new Door("door_1", new Cell(4, 1), DoorKind.Jammed, 0, 1.0);
        var world = TestMaps.World(Map, doors: [far]);
        Assert.Equal(ActionStatus.Invalid, world.Use(null, "door_1").Status);
        Assert.Equal(0, world.Tick);
    }

    [Fact]
    public void Using_item_not_held_is_invalid()
    {
        var world = TestMaps.World(Map, doors: [Locked()]);
        Assert.Equal(ActionStatus.Invalid, world.Use("key_1", "door_1").Status);
    }

    [Fact]
    public void Open_door_does_not_block_sight()
    {
        var world = TestMaps.World(Map, doors: [Jammed(1.0)], sightRadius: 5);
        world.Use(null, "door_1");
        Assert.True(world.IsVisible(new Cell(3, 1)));
    }
}
