using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Tests;

public class GuardTests
{
    private static readonly DungeonMap Map = TestMaps.Corridor;

    // Guard at (4,1); detection radius 2 → agent becomes visible to it at x >= 2.
    private static Guard NewGuard(GuardTraits traits) => new("guard_1", new Cell(4, 1), traits);

    private static DungeonWorld WorldWith(GuardTraits traits, params Item[] items) =>
        TestMaps.World(Map, guards: [NewGuard(traits)], items: items);

    private static Guard G(DungeonWorld w) => w.Guards.Single();

    [Fact]
    public void Guard_is_idle_while_agent_is_out_of_detection_range()
    {
        var world = WorldWith(TestMaps.Traits());
        world.Wait();
        Assert.Equal(GuardState.Idle, G(world).State);
    }

    [Fact]
    public void Guard_becomes_alert_when_agent_enters_detection_range()
    {
        var world = WorldWith(TestMaps.Traits());
        world.Move(Direction.E);
        Assert.Equal(GuardState.Alert, G(world).State);
    }

    [Fact]
    public void Alert_guard_blocks_movement()
    {
        var world = WorldWith(TestMaps.Traits(patience: 10));
        world.Move(Direction.E);
        world.Move(Direction.E);
        var result = world.Move(Direction.E);
        Assert.Equal(ActionStatus.Failed, result.Status);
        Assert.Contains("guard_1", result.Message);
    }

    [Fact]
    public void Ignored_alert_guard_turns_hostile_and_fails_episode()
    {
        var world = WorldWith(TestMaps.Traits(patience: 3));
        world.Move(Direction.E); // alert, patience 3
        world.Wait();            // 2
        world.Wait();            // 1
        Assert.Equal(EpisodeStatus.Running, world.Status);
        world.Wait();            // 0 → hostile
        Assert.Equal(GuardState.Hostile, G(world).State);
        Assert.Equal(EpisodeStatus.Failed, world.Status);
        Assert.Contains("guard_1", world.FailureReason);
    }

    [Fact]
    public void Retreating_out_of_range_resets_alert_guard()
    {
        var world = WorldWith(TestMaps.Traits(patience: 3));
        world.Move(Direction.E);
        world.Wait();
        world.Move(Direction.W);
        Assert.Equal(GuardState.Idle, G(world).State);
        Assert.Equal(3, G(world).Patience);
    }

    [Theory]
    [InlineData(1.0, GuardState.Calm)]
    [InlineData(0.0, GuardState.Alert)]
    public void Talk_calms_guard_with_calm_chance(double chance, GuardState expected)
    {
        var world = WorldWith(TestMaps.Traits(calm: chance, patience: 5));
        world.Move(Direction.E);
        world.Move(Direction.E);
        world.Talk("guard_1");
        Assert.Equal(expected, G(world).State);
    }

    [Fact]
    public void Calm_guard_lets_agent_pass_to_exit()
    {
        var world = WorldWith(TestMaps.Traits(calm: 1.0, patience: 5));
        world.Move(Direction.E);
        world.Move(Direction.E);
        world.Talk("guard_1");
        for (var i = 0; i < 6; i++)
            Assert.Equal(ActionStatus.Success, world.Move(Direction.E).Status);
        Assert.Equal(EpisodeStatus.Succeeded, world.Status);
    }

    [Fact]
    public void Talk_out_of_range_is_invalid_and_free()
    {
        var world = WorldWith(TestMaps.Traits(calm: 1.0));
        Assert.Equal(ActionStatus.Invalid, world.Talk("guard_1").Status);
        Assert.Equal(0, world.Tick);
    }

    [Fact]
    public void Give_food_distracts_for_configured_ticks_then_guard_realerts()
    {
        var world = WorldWith(TestMaps.Traits(food: 1.0, distractTicks: 3, patience: 5),
            new Item("food_1", ItemType.Food, new Cell(3, 1)));
        world.Move(Direction.E);
        world.Move(Direction.E);
        world.PickUp();
        Assert.Equal(ActionStatus.Success, world.Give("food_1", "guard_1").Status);
        Assert.Empty(world.Items);

        // EndTick of the give action already consumed one distracted tick.
        Assert.Equal(GuardState.Distracted, G(world).State);
        world.Wait();
        Assert.Equal(GuardState.Distracted, G(world).State);
        world.Wait();
        Assert.Equal(GuardState.Alert, G(world).State);
    }

    [Fact]
    public void Distracted_guard_does_not_block()
    {
        var world = WorldWith(TestMaps.Traits(food: 1.0, distractTicks: 6, patience: 5),
            new Item("food_1", ItemType.Food, new Cell(3, 1)));
        world.Move(Direction.E);
        world.Move(Direction.E);
        world.PickUp();
        world.Give("food_1", "guard_1");
        Assert.Equal(ActionStatus.Success, world.Move(Direction.E).Status);
        Assert.Equal(new Cell(4, 1), world.AgentPosition);
    }

    [Fact]
    public void Refused_food_is_kept()
    {
        var world = WorldWith(TestMaps.Traits(food: 0.0, patience: 5),
            new Item("food_1", ItemType.Food, new Cell(3, 1)));
        world.Move(Direction.E);
        world.Move(Direction.E);
        world.PickUp();
        Assert.Equal(ActionStatus.Failed, world.Give("food_1", "guard_1").Status);
        Assert.Single(world.Inventory);
    }

    [Fact]
    public void Coin_bribes_guard_permanently()
    {
        var world = WorldWith(TestMaps.Traits(coin: 1.0, patience: 5),
            new Item("coin_1", ItemType.Coin, new Cell(3, 1)));
        world.Move(Direction.E);
        world.Move(Direction.E);
        world.PickUp();
        world.Give("coin_1", "guard_1");
        for (var i = 0; i < 20; i++)
            world.Wait();
        Assert.Equal(GuardState.Bribed, G(world).State);
    }

    [Fact]
    public void Give_requires_adjacency()
    {
        var world = WorldWith(TestMaps.Traits(food: 1.0, patience: 5),
            TestMaps.AtStart(Map, "food_1", ItemType.Food));
        world.PickUp();
        world.Move(Direction.E);
        Assert.Equal(ActionStatus.Invalid, world.Give("food_1", "guard_1").Status);
    }

    [Fact]
    public void Throwing_item_near_guard_distracts_it_from_a_distance()
    {
        var world = WorldWith(TestMaps.Traits(toss: 1.0, patience: 5),
            new Item("food_1", ItemType.Food, new Cell(2, 1)));
        world.Move(Direction.E);
        world.PickUp();
        // Agent at (2,1), 2 cells from the guard: out of give range, but within throw range of (5,1).
        var result = world.Throw("food_1", new Cell(5, 1));
        Assert.Equal(ActionStatus.Success, result.Status);
        Assert.Equal(GuardState.Distracted, G(world).State);
        Assert.Equal(ActionStatus.Success, world.Move(Direction.E).Status);
        Assert.Equal(ActionStatus.Success, world.Move(Direction.E).Status);
        Assert.Equal(new Cell(4, 1), world.AgentPosition);
    }

    [Fact]
    public void Throw_with_zero_chance_does_not_distract()
    {
        var world = WorldWith(TestMaps.Traits(toss: 0.0, patience: 5),
            new Item("food_1", ItemType.Food, new Cell(2, 1)));
        world.Move(Direction.E);
        world.PickUp();
        Assert.Equal(ActionStatus.Failed, world.Throw("food_1", new Cell(3, 1)).Status);
        Assert.Equal(GuardState.Alert, G(world).State);
    }

    [Fact]
    public void Bribed_guard_cannot_be_persuaded_again()
    {
        var world = WorldWith(TestMaps.Traits(calm: 1.0, coin: 1.0, patience: 5),
            new Item("coin_1", ItemType.Coin, new Cell(3, 1)));
        world.Move(Direction.E);
        world.Move(Direction.E);
        world.PickUp();
        world.Give("coin_1", "guard_1");
        Assert.Equal(ActionStatus.Failed, world.Talk("guard_1").Status);
        Assert.Equal(GuardState.Bribed, G(world).State);
    }
}
