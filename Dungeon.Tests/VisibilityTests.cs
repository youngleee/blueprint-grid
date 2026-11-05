using Dungeon.Core;

namespace Dungeon.Tests;

public class VisibilityTests
{
    private static readonly DungeonMap Open = TestMaps.Parse(
        "#########",
        "#.......#",
        "#.......#",
        "#.......#",
        "#...S...#",
        "#.......#",
        "#.......#",
        "#......X#",
        "#########");

    [Fact]
    public void Open_room_sees_full_square_of_radius()
    {
        var visible = Visibility.VisibleFrom(Open, Open.Start, 2);
        Assert.Equal(25, visible.Count);
        Assert.Contains(new Cell(2, 2), visible);
        Assert.Contains(new Cell(6, 6), visible);
    }

    [Fact]
    public void Cells_beyond_radius_are_not_visible()
    {
        var visible = Visibility.VisibleFrom(Open, Open.Start, 2);
        Assert.DoesNotContain(new Cell(7, 4), visible);
        Assert.DoesNotContain(new Cell(4, 7), visible);
    }

    [Fact]
    public void Wall_blocks_sight_but_is_itself_visible()
    {
        var map = TestMaps.Parse(
            "#######",
            "#S.#.X#",
            "#######");
        var visible = Visibility.VisibleFrom(map, map.Start, 5);
        Assert.Contains(new Cell(3, 1), visible);
        Assert.DoesNotContain(new Cell(4, 1), visible);
        Assert.DoesNotContain(map.Exit, visible);
    }

    [Fact]
    public void Origin_is_always_visible()
    {
        Assert.Contains(Open.Start, Visibility.VisibleFrom(Open, Open.Start, 0));
    }

    [Fact]
    public void Line_of_sight_is_symmetric_in_open_space()
    {
        var a = new Cell(1, 1);
        var b = new Cell(6, 3);
        Assert.True(Visibility.HasLineOfSight(Open, a, b));
        Assert.True(Visibility.HasLineOfSight(Open, b, a));
    }
}
