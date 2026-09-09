using System;
using System.Collections.Generic;

namespace GridBlueprint.Model;

// Provides the first manually created composite skill.
public static class ManualSkills
{
    public static CompositeAction MoveDiagonalDownRight { get; } =
        new("move_diagonal_down_right", new[] { "move_down", "move_right" });

    public static CompositeAction MoveDiagonalDownLeft { get; } =
        new("move_diagonal_down_left", new[] { "move_down", "move_left" });

    public static CompositeAction MoveDiagonalUpRight { get; } =
        new("move_diagonal_up_right", new[] { "move_up", "move_right" });

    public static CompositeAction MoveDiagonalUpLeft { get; } =
        new("move_diagonal_up_left", new[] { "move_up", "move_left" });

    public static CompositeAction Get(string name)
    {
        return name switch
        {
            "move_diagonal_down_right" => MoveDiagonalDownRight,
            "move_diagonal_down_left" => MoveDiagonalDownLeft,
            "move_diagonal_up_right" => MoveDiagonalUpRight,
            "move_diagonal_up_left" => MoveDiagonalUpLeft,
            _ => throw new ArgumentException($"Unknown manual skill: {name}")
        };
    }
}
