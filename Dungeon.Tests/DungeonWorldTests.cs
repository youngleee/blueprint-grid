using Dungeon.Core;

namespace Dungeon.Tests;

public class DungeonWorldTests
{
    private static readonly DungeonMap Corridor = TestMaps.Parse(
        "##########",
        "#S......X#",
        "##########");

    [Fact]
    public void Move_into_floor_advances_position_and_tick()
    {
        var world = new DungeonWorld(Corridor, seed: 1, sightRadius: 2);
        Assert.Equal(StepOutcome.Moved, world.Move(Direction.E));
        Assert.Equal(new Cell(2, 1), world.AgentPosition);
        Assert.Equal(1, world.Tick);
    }

    [Fact]
    public void Move_into_wall_is_blocked_but_still_costs_a_tick()
    {
        var world = new DungeonWorld(Corridor, seed: 1, sightRadius: 2);
        Assert.Equal(StepOutcome.Blocked, world.Move(Direction.N));
        Assert.Equal(Corridor.Start, world.AgentPosition);
        Assert.Equal(1, world.Tick);
    }

    [Fact]
    public void Wait_costs_a_tick()
    {
        var world = new DungeonWorld(Corridor, seed: 1);
        world.Wait();
        Assert.Equal(1, world.Tick);
    }

    [Fact]
    public void Reaching_exit_sets_AtExit()
    {
        var world = new DungeonWorld(Corridor, seed: 1);
        for (var i = 0; i < 7; i++)
            world.Move(Direction.E);
        Assert.True(world.AtExit);
    }

    [Fact]
    public void Known_cells_accumulate_as_agent_explores()
    {
        var world = new DungeonWorld(Corridor, seed: 1, sightRadius: 1);
        Assert.False(world.IsKnown(Corridor.Exit));
        var before = world.KnownCells().Count();
        for (var i = 0; i < 7; i++)
            world.Move(Direction.E);
        Assert.True(world.IsKnown(Corridor.Exit));
        Assert.True(world.IsKnown(Corridor.Start), "memory must keep cells that left the sight radius");
        Assert.True(world.KnownCells().Count() > before);
    }

    [Fact]
    public void Clone_is_independent_of_original()
    {
        var world = new DungeonWorld(Corridor, seed: 1, sightRadius: 1);
        world.Move(Direction.E);
        var clone = world.Clone();

        clone.Move(Direction.E);
        clone.Move(Direction.E);

        Assert.Equal(new Cell(2, 1), world.AgentPosition);
        Assert.Equal(1, world.Tick);
        Assert.False(world.IsKnown(new Cell(5, 1)));
        Assert.Equal(new Cell(4, 1), clone.AgentPosition);
        Assert.True(clone.IsKnown(new Cell(5, 1)));
    }

    [Fact]
    public void Clone_continues_rng_streams_identically()
    {
        var world = new DungeonWorld(Corridor, seed: 3);
        world.OutcomeRng.NextULong();
        var clone = world.Clone();
        Assert.Equal(world.OutcomeRng.NextULong(), clone.OutcomeRng.NextULong());
        Assert.Equal(world.EntityRng.NextULong(), clone.EntityRng.NextULong());
    }

    [Fact]
    public void Generated_world_starts_at_map_start()
    {
        var world = DungeonWorld.Create(seed: 11);
        Assert.Equal(world.Map.Start, world.AgentPosition);
        Assert.Contains(world.Map.Start, world.Visible);
    }
}
