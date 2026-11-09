using Dungeon.Core;
using Dungeon.Entities;
using Dungeon.Render;

namespace Dungeon.Tests;

public class CommandInterpreterTests
{
    private static DungeonWorld NewWorld() => TestMaps.World(TestMaps.Corridor,
        doors: [new Door("door_1", new Cell(3, 1), DoorKind.Jammed, 0, 1.0)],
        items: [TestMaps.AtStart(TestMaps.Corridor, "food_1", ItemType.Food)]);

    [Theory]
    [InlineData("move E")]
    [InlineData("e")]
    [InlineData("MOVE right")]
    public void Move_aliases_all_move_east(string command)
    {
        var world = NewWorld();
        CommandInterpreter.Execute(world, command);
        Assert.Equal(new Cell(2, 1), world.AgentPosition);
    }

    [Fact]
    public void Action_output_contains_result_map_and_status()
    {
        var output = CommandInterpreter.Execute(NewWorld(), "pick_up").Output;
        Assert.Contains("pick_up -> success", output);
        Assert.Contains("@", output);
        Assert.Contains("inventory: food_1", output);
    }

    [Fact]
    public void Use_none_pushes_door()
    {
        var world = NewWorld();
        CommandInterpreter.Execute(world, "e");
        var output = CommandInterpreter.Execute(world, "use none door_1").Output;
        Assert.Contains("use(none,door_1) -> success", output);
        Assert.True(world.Doors.Single().IsOpen);
    }

    [Fact]
    public void Throw_parses_coordinates()
    {
        var world = NewWorld();
        CommandInterpreter.Execute(world, "pick_up");
        CommandInterpreter.Execute(world, "throw food_1 2 1");
        Assert.Equal(new Cell(2, 1), world.Items.Single().Position);
    }

    [Theory]
    [InlineData("map")]
    [InlineData("reveal")]
    [InlineData("state")]
    [InlineData("debug")]
    [InlineData("help")]
    public void Inspect_commands_are_free(string command)
    {
        var world = NewWorld();
        CommandInterpreter.Execute(world, command);
        Assert.Equal(0, world.Tick);
    }

    [Theory]
    [InlineData("fly", "unknown command")]
    [InlineData("move", "missing <direction>")]
    [InlineData("move Q", "bad direction")]
    [InlineData("throw food_1 a 1", "must be a number")]
    public void Bad_input_reports_error_without_spending_a_tick(string command, string message)
    {
        var world = NewWorld();
        var output = CommandInterpreter.Execute(world, command).Output;
        Assert.StartsWith("error:", output);
        Assert.Contains(message, output);
        Assert.Equal(0, world.Tick);
    }

    [Fact]
    public void Quit_sets_quit_flag()
    {
        Assert.True(CommandInterpreter.Execute(NewWorld(), "quit").Quit);
    }
}
