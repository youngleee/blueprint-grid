using System.Text.RegularExpressions;
using Dungeon.Agent;

namespace Dungeon.Skills;

// ClosedLoop: a real skill (PLAN §3) — parameterized, with at least one fact-dependent branch or loop.
// Macro: the ablation of PLAN §7.3 — a fixed list of calls, no parameters, no branches or loops.
public enum SkillForm { ClosedLoop, Macro }

public sealed record ValidationOptions(
    SkillForm Form = SkillForm.ClosedLoop,
    int MaxLoop = 6,
    int MaxNesting = 5,
    int MaxWorstCaseCalls = 40);

public sealed record ValidationResult(IReadOnlyList<string> Errors)
{
    public bool Ok => Errors.Count == 0;
    public override string ToString() => Ok ? "valid" : string.Join("; ", Errors);
}

// Static checks before a skill may be tested or registered (PLAN §6.4 step 1). Reports every
// problem at once, with locations, because the list is fed back to the LLM.
public static partial class SkillValidator
{
    public static readonly HashSet<string> StateWords =
        ["idle", "alert", "calm", "distracted", "bribed", "hostile", "open", "closed", "held", "on_floor"];

    public static readonly HashSet<string> TypeWords =
        ["guard", "locked", "jammed", "key", "lockpick", "food", "coin", "tool"];

    private static readonly HashSet<string> ItemTypeWords = ["key", "lockpick", "food", "coin", "tool"];

    private enum V { Guard, Door, Item, Target, Int, Dir, None, Exit, Unknown }

    public static ValidationResult Validate(Skill skill, ActionSpace space, IEnumerable<Skill>? existing = null,
        ValidationOptions? options = null)
    {
        var o = options ?? new ValidationOptions();
        var macro = o.Form == SkillForm.Macro;
        var errors = new List<string>();
        var ps = skill.Params.ToDictionary(p => p.Name, p => p.Type);
        var used = new HashSet<string>();

        if (space.Get(skill.Name) is not null)
            errors.Add($"name: '{skill.Name}' is already an action");
        foreach (var p in skill.Params.Where(p => StateWords.Contains(p.Name) || TypeWords.Contains(p.Name)))
            errors.Add($"params: '{p.Name}' is a fact word; choose another parameter name");
        if (macro && skill.Params.Count > 0)
            errors.Add("params: a macro takes no parameters");
        if (skill.Effect.Count == 0)
            errors.Add("effect: must declare at least one condition (what the skill achieves)");

        for (var i = 0; i < skill.Pre.Count; i++)
            CheckCondition(skill.Pre[i].Parsed, $"pre[{i}]", ps, used, macro, errors);
        for (var i = 0; i < skill.Effect.Count; i++)
            CheckCondition(skill.Effect[i].Parsed, $"effect[{i}]", ps, used, macro, errors);

        var closedLoop = 0;
        CheckBlock(skill.Body, "body", 1);

        void CheckBlock(IReadOnlyList<SkillStmt> block, string path, int depth)
        {
            if (depth > o.MaxNesting)
            {
                errors.Add($"{path}: nested deeper than {o.MaxNesting} levels");
                return;
            }
            for (var i = 0; i < block.Count; i++)
            {
                var p = $"{path}[{i}]";
                switch (block[i])
                {
                    case CallStmt c:
                        CheckCall(c, p);
                        break;
                    case IfStmt f:
                        if (macro) errors.Add($"{p}: a macro may not branch ('if')");
                        if (ReadsFacts(f.Condition.Parsed)) closedLoop++;
                        CheckCondition(f.Condition.Parsed, $"{p}.if", ps, used, macro, errors);
                        CheckBlock(f.Then, $"{p}.then", depth + 1);
                        CheckBlock(f.Else, $"{p}.else", depth + 1);
                        break;
                    case UntilStmt u:
                        if (macro) errors.Add($"{p}: a macro may not loop ('until')");
                        if (u.Max > o.MaxLoop) errors.Add($"{p}.max: at most {o.MaxLoop}");
                        if (ReadsFacts(u.Condition.Parsed)) closedLoop++;
                        CheckCondition(u.Condition.Parsed, $"{p}.until", ps, used, macro, errors);
                        CheckBlock(u.Body, $"{p}.do", depth + 1);
                        break;
                    case RepeatStmt r:
                        if (macro) errors.Add($"{p}: a macro may not loop ('repeat')");
                        if (r.Count > o.MaxLoop) errors.Add($"{p}.repeat: at most {o.MaxLoop}");
                        CheckBlock(r.Body, $"{p}.do", depth + 1);
                        break;
                    case FailStmt:
                        if (macro) errors.Add($"{p}: a macro may only call actions");
                        break;
                }
            }
        }

        void CheckCall(CallStmt c, string path)
        {
            if (c.Action == skill.Name)
            {
                errors.Add($"{path}: '{c.Action}' calls itself; recursion is not allowed");
                return;
            }
            var callee = space.Get(c.Action);
            if (callee is null)
            {
                errors.Add($"{path}.call: unknown action '{c.Action}' (available: {string.Join(", ", space.Actions.Select(a => a.Name))})");
                return;
            }

            var values = new List<V>();
            for (var i = 0; i < c.Args.Count; i++)
                values.AddRange(ArgTypes(c.Args[i], $"{path}.args[{i}]", ps, used, macro, errors));

            var required = callee.Params.Count(p => !p.Optional);
            if (values.Count < required || values.Count > callee.Params.Count)
            {
                var expected = required == callee.Params.Count ? $"{required}" : $"{required}-{callee.Params.Count}";
                errors.Add($"{path}: {callee.Signature} takes {expected} argument(s), got {values.Count}" +
                           (c.Args.Any(a => a is FuncArg { Function: "pos" }) ? " (pos(x) counts as two: x and y)" : ""));
                return;
            }
            for (var i = 0; i < values.Count; i++)
                if (!Compatible(callee.Params[i].Type, values[i]))
                    errors.Add($"{path}.args: argument {i + 1} of {c.Action} ({callee.Params[i].Name}) must be {Describe(callee.Params[i].Type)}, got {Describe(values[i])}");
        }

        foreach (var p in skill.Params.Where(p => !used.Contains(p.Name)))
            errors.Add($"params: '{p.Name}' is never used");

        if (!macro && closedLoop == 0)
            errors.Add("body: needs at least one 'if' or 'until' that checks the situation; " +
                       "a fixed sequence of calls is a macro, not a skill");

        var worst = WorstCaseCalls(skill.Body);
        if (worst > o.MaxWorstCaseCalls)
            errors.Add($"body: up to {worst} calls in the worst case (limit {o.MaxWorstCaseCalls}); lower the loop maxima");

        if (existing is not null && errors.Count == 0)
        {
            var shape = Shape(skill);
            var twin = existing.FirstOrDefault(e => Shape(e) == shape);
            if (twin is not null)
                errors.Add($"skill: same parameters, contract and body as existing skill '{twin.Name}'");
        }

        return new ValidationResult(errors);
    }

    public static int WorstCaseCalls(IReadOnlyList<SkillStmt> block) => block.Sum(s => s switch
    {
        CallStmt => 1,
        IfStmt f => Math.Max(WorstCaseCalls(f.Then), WorstCaseCalls(f.Else)),
        UntilStmt u => u.Max * WorstCaseCalls(u.Body),
        RepeatStmt r => r.Count * WorstCaseCalls(r.Body),
        _ => 0
    });

    public static bool ReadsFacts(Condition c) => c switch
    {
        Const => false,
        Not n => ReadsFacts(n.Inner),
        And a => a.Parts.Any(ReadsFacts),
        Or x => x.Parts.Any(ReadsFacts),
        _ => true
    };

    private static void CheckCondition(Condition c, string path, IReadOnlyDictionary<string, ParamType> ps,
        HashSet<string> used, bool macro, List<string> errors)
    {
        switch (c)
        {
            case Not n:
                CheckCondition(n.Inner, path, ps, used, macro, errors);
                return;
            case And a:
                foreach (var p in a.Parts) CheckCondition(p, path, ps, used, macro, errors);
                return;
            case Or x:
                foreach (var p in x.Parts) CheckCondition(p, path, ps, used, macro, errors);
                return;
            case Predicate p:
                foreach (var term in p.Args.Concat(p.Values))
                {
                    if (int.TryParse(term, out _) || term == "exit" || StateWords.Contains(term) || TypeWords.Contains(term))
                        continue;
                    if (ps.ContainsKey(term))
                    {
                        used.Add(term);
                        continue;
                    }
                    if (EntityId().IsMatch(term))
                    {
                        if (!macro)
                            errors.Add($"{path}: names the specific entity '{term}'; use a parameter so the skill works for any {Kind(term)}");
                        continue;
                    }
                    errors.Add($"{path}: unknown name '{term}' (not a parameter or fact word)");
                }
                return;
        }
    }

    private static IEnumerable<V> ArgTypes(ArgExpr a, string path, IReadOnlyDictionary<string, ParamType> ps,
        HashSet<string> used, bool macro, List<string> errors)
    {
        V? Param(string name)
        {
            if (!ps.TryGetValue(name, out var t))
                return null;
            used.Add(name);
            return t switch
            {
                ParamType.Guard => V.Guard,
                ParamType.Door => V.Door,
                ParamType.Item => V.Item,
                ParamType.Target => V.Target,
                ParamType.Int => V.Int,
                ParamType.Direction => V.Dir,
                _ => V.Unknown
            };
        }

        V? Literal(string name)
        {
            if (int.TryParse(name, out _)) return V.Int;
            if (name == "none") return V.None;
            if (name == "exit") return V.Exit;
            if (name is "N" or "E" or "S" or "W") return V.Dir;
            if (EntityId().IsMatch(name))
            {
                if (!macro)
                {
                    errors.Add($"{path}: names the specific entity '{name}'; use a parameter so the skill works for any {Kind(name)}");
                    return V.Unknown;
                }
                return Kind(name) switch { "guard" => V.Guard, "door" => V.Door, _ => V.Item };
            }
            return null;
        }

        V Error(string message)
        {
            errors.Add(message);
            return V.Unknown;
        }

        switch (a)
        {
            case NameArg n:
                return [Param(n.Name) ?? Literal(n.Name) ?? Error($"{path}: unknown name '{n.Name}' (not a parameter or literal)")];
            case FuncArg { Function: "pos" } f:
            {
                var t = Param(f.Args[0]) ?? Literal(f.Args[0]);
                if (t is null or V.Int or V.Dir or V.None)
                    Error($"{path}: pos(x) needs an entity parameter or 'exit'");
                return [V.Int, V.Int];
            }
            case FuncArg { Function: "held" } f:
                if (!ItemTypeWords.Contains(f.Args[0]) && Param(f.Args[0]) != V.Item)
                    Error($"{path}: held(t) needs an item type ({string.Join(", ", ItemTypeWords)}) or an item parameter");
                return [V.Item];
            case FuncArg { Function: "key_for" } f:
                if ((Param(f.Args[0]) ?? Literal(f.Args[0])) != V.Door)
                    Error($"{path}: key_for(d) needs a door parameter");
                return [V.Item];
            default:
                return [Error($"{path}: unsupported argument {a}")];
        }
    }

    private static bool Compatible(ParamType expected, V v) => v == V.Unknown || expected switch
    {
        ParamType.Direction => v == V.Dir,
        ParamType.Guard => v == V.Guard,
        ParamType.Door => v == V.Door,
        ParamType.Item => v == V.Item,
        ParamType.ItemOrNone => v is V.Item or V.None,
        ParamType.Target => v is V.Guard or V.Door or V.Item or V.Target or V.Exit,
        ParamType.Int => v == V.Int,
        _ => false
    };

    private static string Describe(ParamType t) => t switch
    {
        ParamType.Direction => "a direction N/E/S/W",
        ParamType.Guard => "a guard",
        ParamType.Door => "a door",
        ParamType.Item => "an item",
        ParamType.ItemOrNone => "an item or 'none'",
        ParamType.Target => "an entity or 'exit'",
        ParamType.Int => "a number",
        _ => t.ToString()
    };

    private static string Describe(V v) => v switch
    {
        V.Dir => "a direction",
        V.Int => "a number",
        V.None => "'none'",
        V.Exit => "'exit'",
        V.Target => "a target",
        _ => "a " + v.ToString().ToLowerInvariant()
    };

    private static string Kind(string id) => id[..id.LastIndexOf('_')];

    // Same skill under another name: compare everything except name and description.
    private static string Shape(Skill s) => SkillJson.Serialize(s with { Name = "x", Description = "" }, indented: false);

    [GeneratedRegex("^[a-z]+_[0-9]+$")]
    private static partial Regex EntityId();
}
