using Dungeon.Agent;

namespace Dungeon.Skills;

public static class SkillActions
{
    // Wraps a skill as an action of A_t. Its parameters are validated like any other action's;
    // the interpreter then checks the precondition, runs the body and checks the effect.
    public static AgentAction ToAction(Skill skill, SkillLimits? limits = null) =>
        new(skill.Name, ActionKind.Skill,
            skill.Params.Select(p => new ActionParam(p.Name, p.Type)).ToList(),
            Describe(skill),
            (ctx, space, args) => SkillInterpreter.Run(skill, ctx, space, args, limits));

    // What the LLM is told about a skill: its purpose and its contract, not its body.
    public static string Describe(Skill skill)
    {
        var parts = new List<string>();
        if (skill.Description.Length > 0)
            parts.Add(skill.Description.TrimEnd('.') + ".");
        if (skill.Pre.Count > 0)
            parts.Add($"Needs: {string.Join(" and ", skill.Pre.Select(c => c.Text))}.");
        if (skill.Effect.Count > 0)
            parts.Add($"Achieves: {string.Join(" and ", skill.Effect.Select(c => c.Text))}.");
        parts.Add("Skill: one decision, may take several ticks.");
        return string.Join(' ', parts);
    }

    public static ActionSpace WithSkills(this ActionSpace space, IEnumerable<Skill> skills, SkillLimits? limits = null)
    {
        foreach (var s in skills)
            space.Register(ToAction(s, limits));
        return space;
    }
}
