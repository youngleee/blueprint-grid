using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Entities;
using Dungeon.Render;

namespace Dungeon.Tests;

public class CommandInterpreterTests
{
    private static AgentContext NewAgent() => new(TestMaps.World(TestMaps.Corridor,
        doors: [new Door("door_1", new Cell(3, 1), DoorKind.Jammed, 0, 1.0)],
        items: [TestMaps.AtStart(TestMaps.Corridor, "food_1", ItemType.Food)]));

    private static string Run(AgentContext a, string command) => CommandInterpreter.Execute(a, command).Output;

    [Theory]
    [InlineData("move E")]
    [InlineData("e")]
    [InlineData("MOVE right")]
    public void Move_aliases_all_move_east(string command)
    {
        var a = NewAgent();
        Run(a, command);
        Assert.Equal(new Cell(2, 1), a.Position);
    }

    [Fact]
    public void Action_output_contains_result_map_and_status()
    {
        var output = Run(NewAgent(), "pick_up");
        Assert.Contains("pick_up -> success", output);
        Assert.Contains("@", output);
        Assert.Contains("inventory: food_1", output);
    }

    [Fact]
    public void Use_none_pushes_door()
    {
        var a = NewAgent();
        Run(a, "e");
        Assert.Contains("use(none,door_1) -> success", Run(a, "use none door_1"));
        Assert.True(a.World.Doors.Single().IsOpen);
    }

    [Fact]
    public void Throw_parses_coordinates()
    {
        var a = NewAgent();
        Run(a, "pick_up");
        Run(a, "throw food_1 2 1");
        Assert.Equal(new Cell(2, 1), a.World.Items.Single().Position);
    }

    [Fact]
    public void Actions_update_agent_memory()
    {
        var a = NewAgent();
        Run(a, "pick_up");
        Assert.Equal("held", a.Memory.Get("food_1")!.State);
        Assert.Contains("pick_up -> success", a.Memory.LastOutcome);
    }

    [Fact]
    public void Goto_walks_to_known_entity()
    {
        var a = NewAgent();
        var output = Run(a, "goto door_1");
        Assert.Contains("go_to(door_1,1) -> arrived", output);
        Assert.Equal(new Cell(2, 1), a.Position);
    }

    [Fact]
    public void Check_evaluates_conditions()
    {
        var a = NewAgent();
        Assert.Contains("= true", Run(a, "check state(door_1) == closed and not has(food)"));
        Run(a, "pick_up");
        Assert.Contains("= true", Run(a, "check has(food_1)"));
    }

    [Fact]
    public void Check_reports_parse_errors()
    {
        Assert.StartsWith("error:", Run(NewAgent(), "check state(door_1)"));
    }

    [Theory]
    [InlineData("map")]
    [InlineData("reveal")]
    [InlineData("state")]
    [InlineData("debug")]
    [InlineData("facts")]
    [InlineData("check has(food)")]
    [InlineData("help")]
    public void Inspect_commands_are_free(string command)
    {
        var a = NewAgent();
        Run(a, command);
        Assert.Equal(0, a.Tick);
    }

    [Theory]
    [InlineData("fly", "unknown command")]
    [InlineData("move", "missing <direction>")]
    [InlineData("move Q", "bad direction")]
    [InlineData("throw food_1 a 1", "must be a number")]
    [InlineData("goto", "missing <id|exit>")]
    public void Bad_input_reports_error_without_spending_a_tick(string command, string message)
    {
        var a = NewAgent();
        var output = Run(a, command);
        Assert.StartsWith("error:", output);
        Assert.Contains(message, output);
        Assert.Equal(0, a.Tick);
    }

    [Fact]
    public void Quit_sets_quit_flag()
    {
        Assert.True(CommandInterpreter.Execute(NewAgent(), "quit").Quit);
    }
}
