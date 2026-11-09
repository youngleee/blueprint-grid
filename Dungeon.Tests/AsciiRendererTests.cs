using Dungeon.Core;
using Dungeon.Entities;
using Dungeon.Render;

namespace Dungeon.Tests;

public class AsciiRendererTests
{
    [Fact]
    public void Agent_view_hides_unknown_cells_and_entities_out_of_sight()
    {
        var map = TestMaps.Corridor;
        var guard = new Guard("guard_1", new Cell(8, 1), TestMaps.Traits(patience: 9));
        var world = TestMaps.World(map, guards: [guard], sightRadius: 2);

        var view = AsciiRenderer.Render(world);

        Assert.Contains('@', view);
        Assert.DoesNotContain('G', view);
        Assert.Contains('G', AsciiRenderer.Render(world, reveal: true));
    }

    [Fact]
    public void Remembered_cells_show_terrain_but_not_entities()
    {
        var map = TestMaps.Corridor;
        var world = TestMaps.World(map, items: [new Item("food_1", ItemType.Food, new Cell(2, 1))], sightRadius: 1);
        Assert.Equal('f', AsciiRenderer.Glyph(world, new Cell(2, 1), reveal: false));

        world.Move(Direction.E);
        world.Move(Direction.E);
        world.Move(Direction.E);

        Assert.Equal(',', AsciiRenderer.Glyph(world, new Cell(2, 1), reveal: false));
    }

    [Fact]
    public void Status_lists_only_visible_entities()
    {
        var map = TestMaps.Corridor;
        var door = new Door("door_1", new Cell(7, 1), DoorKind.Jammed, 0, 0.5);
        var world = TestMaps.World(map, doors: [door], sightRadius: 2);
        Assert.DoesNotContain("door_1", AsciiRenderer.Status(world));
        Assert.Contains("door_1", AsciiRenderer.Debug(world));
    }
}
