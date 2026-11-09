using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Tests;

public class ActionSpaceTests
{
    private static AgentContext Agent(params Item[] items) =>
        new(TestMaps.World(TestMaps.Corridor,
            guards: [new Guard("guard_1", new Cell(7, 1), TestMaps.Traits(patience: 9))],
            items: items, sightRadius: 2));

    [Fact]
    public void Standard_space_has_8_primitives_and_3_helpers()
    {
        var space = StandardActions.Create();
        Assert.Equal(8, space.Actions.Count(a => a.Kind == ActionKind.Primitive));
        Assert.Equal(["go_to", "explore", "retreat"], space.Actions.Where(a => a.Kind == ActionKind.Helper).Select(a => a.Name));
    }

    [Fact]
    public void Entity_arguments_must_be_known_to_the_agent()
    {
        var a = Agent();
        // guard_1 exists in the world but is out of sight: must look like a non-existent id.
        var unseen = StandardActions.Create().Execute(a, "talk", ["guard_1"]);
        var fake = StandardActions.Create().Execute(a, "talk", ["guard_9"]);
        Assert.True(unseen.IsInvalid);
        Assert.Equal(unseen.Message.Replace("guard_1", "X"), fake.Message.Replace("guard_9", "X"));
    }

    [Fact]
    public void Wrong_entity_kind_is_rejected()
    {
        var a = Agent(TestMaps.AtStart(TestMaps.Corridor, "food_1", ItemType.Food));
        var r = StandardActions.Create().Execute(a, "talk", ["food_1"]);
        Assert.True(r.IsInvalid);
        Assert.Contains("unknown guard", r.Message);
    }

    [Fact]
    public void Execute_records_outcome_as_last_action()
    {
        var a = Agent(TestMaps.AtStart(TestMaps.Corridor, "food_1", ItemType.Food));
        var r = StandardActions.Create().Execute(a, "pick_up", []);
        Assert.Equal("success", r.Status);
        Assert.Equal(1, r.Ticks);
        Assert.Equal(r.ToString(), a.Memory.LastOutcome);
    }

    [Fact]
    public void Helper_outcome_reports_ticks()
    {
        var a = Agent();
        var r = StandardActions.Create().Execute(a, "explore", []);
        Assert.True(r.Ticks > 1);
        Assert.Contains($"after {r.Ticks} ticks", r.ToString());
    }

    [Fact]
    public void Actions_after_episode_end_are_invalid()
    {
        var a = new AgentContext(TestMaps.World(TestMaps.Corridor, sightRadius: 9));
        var space = StandardActions.Create();
        space.Execute(a, "go_to", ["exit"]);
        Assert.True(a.IsOver);
        Assert.True(space.Execute(a, "wait", []).IsInvalid);
    }

    [Fact]
    public void Duplicate_registration_is_rejected()
    {
        var space = StandardActions.Create();
        Assert.Throws<InvalidOperationException>(() => space.Register(space.Get("wait")!));
    }

    [Fact]
    public void Clone_does_not_share_registrations()
    {
        var space = StandardActions.Create();
        var copy = space.Clone();
        copy.Register(space.Get("wait")! with { Name = "rest" });
        Assert.Null(space.Get("rest"));
        Assert.NotNull(copy.Get("rest"));
    }
}
