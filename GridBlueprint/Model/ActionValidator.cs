using System;

namespace GridBlueprint.Model;

// Checks a composite skill before it enters the action registry.
public static class ActionValidator
{
    public static bool TryValidate(
        CompositeAction skill,
        ActionRegistry registry,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(skill.Name))
        {
            error = "Skill name cannot be empty";
            return false;
        }

        if (registry.Contains(skill.Name))
        {
            error = $"Skill name already exists: {skill.Name}";
            return false;
        }

        if (skill.Steps.Count == 0)
        {
            error = "Skill must contain at least one step";
            return false;
        }

        foreach (var step in skill.Steps)
        {
            if (!registry.TryGet(step, out var primitive))
            {
                error = $"Unknown primitive action: {step}";
                return false;
            }

            if (Math.Abs(primitive.DeltaX) + Math.Abs(primitive.DeltaY) != 1)
            {
                error = $"Invalid movement delta: {step}";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}
