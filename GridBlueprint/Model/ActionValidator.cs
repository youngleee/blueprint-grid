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
        if (skill == null || string.IsNullOrWhiteSpace(skill.Name))
        {
            error = "Skill name cannot be empty";
            return false;
        }

        if (skill.Steps == null || skill.Steps.Count == 0 || skill.Steps.Count > CompositeAction.MaxSteps)
        {
            error = $"Skill must contain 1 to {CompositeAction.MaxSteps} steps";
            return false;
        }

        if (registry.Contains(skill.Name))
        {
            error = $"Skill name already exists: {skill.Name}";
            return false;
        }

        if (registry.ContainsCompositeSteps(skill.Steps))
        {
            error = "Skill steps already exist in the same order";
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
