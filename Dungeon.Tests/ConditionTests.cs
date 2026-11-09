using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Tests;

public class ConditionTests
{
    // Agent at (1,1) holding food_1 and key_1; door_1 adjacent and closed; guard_1 alert at distance 2.
    private static FactSet Facts()
    {
        var map = TestMaps.Parse(
            "#######",
            "#S....#",
            "#.....#",
            "#....X#",
            "#######");
        var world = TestMaps.World(map,
            doors: [new Door("door_1", new Cell(1, 2), DoorKind.Locked, 0, 0)],
            guards: [new Guard("guard_1", new Cell(3, 1), TestMaps.Traits(patience: 9))],
            items:
            [
                TestMaps.AtStart(map, "food_1", ItemType.Food),
                new Item("key_1", ItemType.Key, new Cell(2, 1), opens: "door_1")
            ]);
        var a = new AgentContext(world);
        a.Do(w => w.PickUp());
        a.Do(w => w.Move(Direction.E));
        a.Do(w => w.PickUp());
        a.Do(w => w.Move(Direction.W));
        return FactSet.From(a);
    }

    private static bool Eval(string text, Dictionary<string, string>? bindings = null) =>
        Condition.Parse(text).Evaluate(Facts(), bindings);

    [Theory]
    [InlineData("has(food_1)", true)]
    [InlineData("has(food)", true)]
    [InlineData("has(coin)", false)]
    [InlineData("count(food) == 1", true)]
    [InlineData("visible(guard_1)", true)]
    [InlineData("known(guard_9)", false)]
    [InlineData("adjacent(door_1)", true)]
    [InlineData("adjacent(guard_1)", false)]
    [InlineData("state(guard_1) == alert", true)]
    [InlineData("state(guard_1) == ALERT", true)]
    [InlineData("state(guard_1) != hostile", true)]
    [InlineData("state(guard_1) in [calm, bribed, distracted]", false)]
    [InlineData("state(door_1) in [closed]", true)]
    [InlineData("type(door_1) == locked", true)]
    [InlineData("type(key_1) == key", true)]
    [InlineData("dist(guard_1) <= 2", true)]
    [InlineData("dist(guard_1) > 2", false)]
    [InlineData("at_exit()", false)]
    [InlineData("known(exit)", true)]   // seen from (2,1)
    [InlineData("visible(exit)", false)]
    [InlineData("dist(exit) == 4", true)]
    [InlineData("true", true)]
    public void Predicates_evaluate_against_facts(string text, bool expected)
    {
        Assert.Equal(expected, Eval(text));
    }

    [Theory]
    [InlineData("has(food) and visible(guard_1)", true)]
    [InlineData("has(coin) or visible(guard_1)", true)]
    [InlineData("not has(coin)", true)]
    [InlineData("not(has(food))", false)]
    [InlineData("and(has(food), has(key), adjacent(door_1))", true)]
    [InlineData("or(has(coin), state(guard_1) == calm)", false)]
    [InlineData("has(coin) or has(food) and has(key)", true)] // and binds tighter
    [InlineData("(has(coin) or has(food)) and has(lockpick)", false)]
    public void Boolean_combinators(string text, bool expected)
    {
        Assert.Equal(expected, Eval(text));
    }

    [Fact]
    public void Unknown_entities_make_comparisons_false_not_errors()
    {
        Assert.False(Eval("state(guard_9) == alert"));
        Assert.False(Eval("state(guard_9) != alert"));
        Assert.False(Eval("dist(guard_9) < 100"));
    }

    [Fact]
    public void Parameters_are_resolved_through_bindings()
    {
        var b = new Dictionary<string, string> { ["g"] = "guard_1", ["it"] = "food_1" };
        Assert.True(Eval("visible(g) and has(it) and state(g) != hostile", b));
        Assert.False(Eval("visible(g)", new() { ["g"] = "guard_9" }));
    }

    [Fact]
    public void Identifiers_lists_terms_for_validation()
    {
        var c = Condition.Parse("visible(g) and dist(g) <= 2 and state(g) in [alert, idle]");
        Assert.Equal(["alert", "g", "g", "g", "idle"], c.Identifiers().Order().ToList());
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("flies(guard_1)", "unknown predicate")]
    [InlineData("state(guard_1)", "needs a comparison")]
    [InlineData("has(food) == true", "cannot be compared")]
    [InlineData("state(guard_1) < 3", "not a number")]
    [InlineData("has(food, coin)", "takes 1 argument")]
    [InlineData("has(food) and", "unexpected end")]
    [InlineData("has(food))", "unexpected ')'")]
    [InlineData("has(food) && has(key)", "unexpected '&'")]
    [InlineData("not(has(food), has(key))", "exactly one")]
    public void Malformed_conditions_are_rejected_with_a_message(string text, string message)
    {
        Assert.False(Condition.TryParse(text, out _, out var error));
        Assert.Contains(message, error);
    }

    [Fact]
    public void ToString_round_trips_through_parse()
    {
        var original = "and(visible(g), or(state(g) in [alert, idle], dist(g) <= 2), not(has(coin)))";
        var once = Condition.Parse(original).ToString();
        Assert.Equal(once, Condition.Parse(once).ToString());
    }
}
