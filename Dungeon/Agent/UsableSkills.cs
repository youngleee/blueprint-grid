namespace Dungeon.Agent;

// True if calling the action with these arguments now would do something useful: for skills,
// the precondition holds and the effect is not already reached.
public delegate bool Applicability(FactSet facts, IReadOnlyList<string> args);

// Lists skill calls that are useful right now, binding each entity parameter to every
// known entity of its kind. Only skills with at most two entity parameters are expanded, which
// keeps the list short; the rest are omitted (they can still be called).
public static class UsableSkills
{
    public static IReadOnlyList<string>? List(FactSet facts, ActionSpace space)
    {
        var skills = space.Actions.Where(a => a.Kind == ActionKind.Skill).ToList();
        if (skills.Count == 0)
            return null;

        var result = new List<string>();
        foreach (var skill in skills)
        {
            if (skill.Applies is null || skill.Params.Count > 2)
                continue;
            foreach (var args in Bindings(facts, skill.Params))
                if (skill.Applies(facts, args))
                    result.Add(args.Count == 0 ? skill.Name : $"{skill.Name}({string.Join(", ", args)})");
        }
        return result;
    }

    private static IEnumerable<IReadOnlyList<string>> Bindings(FactSet facts, IReadOnlyList<ActionParam> ps)
    {
        IEnumerable<string> Candidates(ParamType t) => t switch
        {
            ParamType.Guard => facts.Entities.Where(e => e.Sighting.Kind == EntityKind.Guard).Select(e => e.Sighting.Id),
            ParamType.Door => facts.Entities.Where(e => e.Sighting.Kind == EntityKind.Door).Select(e => e.Sighting.Id),
            ParamType.Item => facts.Inventory.Select(s => s.Id).Concat(facts.Entities.Where(e => e.Sighting.Kind == EntityKind.Item).Select(e => e.Sighting.Id)),
            ParamType.Target => facts.Entities.Select(e => e.Sighting.Id).Concat(facts.Exit is null ? [] : ["exit"]),
            _ => []
        };

        IEnumerable<List<string>> Expand(int i)
        {
            if (i == ps.Count)
            {
                yield return [];
                yield break;
            }
            foreach (var c in Candidates(ps[i].Type))
                foreach (var rest in Expand(i + 1))
                    yield return [c, .. rest];
        }

        return Expand(0);
    }
}
