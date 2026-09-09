using System.Collections.Generic;

namespace GridBlueprint.Model;

// Provides the first manually created composite skill.
public static class ManualSkills
{
    public static CompositeAction MoveDiagonalDownRight { get; } =
        new("move_diagonal_down_right", new[] { "move_down", "move_right" });
}
