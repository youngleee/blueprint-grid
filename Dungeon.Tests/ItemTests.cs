using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Tests;

public class ItemTests
{
    private static readonly DungeonMap Map = TestMaps.Corridor;

    [Fact]
    public void Pick_up_moves_item_into_inventory_and_costs_a_tick()
    {
        var world = TestMaps.World(Map, items: [TestMaps.AtStart(Map, "coin_1", ItemType.Coin)]);
        var result = world.PickUp();
        Assert.Equal(ActionStatus.Success, result.Status);
        Assert.Equal(["coin_1"], world.Inventory.Select(i => i.Id));
        Assert.Equal(1, world.Tick);
    }

    [Fact]
    public void Pick_up_on_empty_cell_is_invalid_and_free()
    {
        var world = TestMaps.World(Map);
        Assert.Equal(ActionStatus.Invalid, world.PickUp().Status);
        Assert.Equal(0, world.Tick);
    }

    [Fact]
    public void Pick_up_takes_only_the_item_on_the_agent_cell()
    {
        var world = TestMaps.World(Map, items: [new Item("food_1", ItemType.Food, new Cell(2, 1))]);
        Assert.Equal(ActionStatus.Invalid, world.PickUp().Status);
        world.Move(Direction.E);
        Assert.Equal(ActionStatus.Success, world.PickUp().Status);
    }

    [Fact]
    public void Throw_places_item_on_target_cell()
    {
        var world = TestMaps.World(Map, items: [TestMaps.AtStart(Map, "food_1", ItemType.Food)]);
        world.PickUp();
        var result = world.Throw("food_1", new Cell(4, 1));
        Assert.Equal(ActionStatus.Failed, result.Status); // nobody to distract
        Assert.Empty(world.Inventory);
        Assert.Equal(new Cell(4, 1), world.Items.Single().Position);
    }

    [Theory]
    [InlineData(5, 1)] // beyond throw range 3
    [InlineData(1, 0)] // wall
    public void Throw_to_invalid_target_is_rejected(int x, int y)
    {
        var world = TestMaps.World(Map, items: [TestMaps.AtStart(Map, "food_1", ItemType.Food)]);
        world.PickUp();
        Assert.Equal(ActionStatus.Invalid, world.Throw("food_1", new Cell(x, y)).Status);
        Assert.Single(world.Inventory);
    }

    [Fact]
    public void Clone_does_not_share_inventory()
    {
        var world = TestMaps.World(Map, items: [TestMaps.AtStart(Map, "food_1", ItemType.Food)]);
        var clone = world.Clone();
        clone.PickUp();
        Assert.Empty(world.Inventory);
        Assert.Single(clone.Inventory);
    }
}
