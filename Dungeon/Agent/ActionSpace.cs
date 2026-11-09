using Dungeon.Core;

namespace Dungeon.Agent;

public enum ParamType { Direction, Guard, Door, Item, ItemOrNone, Target, Int }

public sealed record ActionParam(string Name, ParamType Type, bool Optional = false)
{
    public override string ToString() => Optional ? $"[{Name}]" : Name;
}

// Uniform result of one decision, whatever kind of action it was.
public sealed record ActionOutcome(string Call, string Status, int Ticks, string Message)
{
    public bool IsInvalid => Status == "invalid";

    public override string ToString() =>
        Ticks > 1 ? $"{Call} -> {Status} after {Ticks} ticks: {Message}" : $"{Call} -> {Status}: {Message}";

    public static ActionOutcome From(ActionResult r, int ticks) =>
        new(r.Action, r.Status.ToString().ToLowerInvariant(), ticks, r.Message);

    public static ActionOutcome From(HelperResult r) =>
        new(r.Action, r.Status == HelperStatus.EpisodeOver ? "episode_over" : r.Status.ToString().ToLowerInvariant(), r.Ticks, r.Message);
}

public enum ActionKind { Primitive, Helper, Skill }

// One entry of the agent's action set A_t. Arguments arrive validated and resolved.
public sealed record AgentAction(
    string Name,
    ActionKind Kind,
    IReadOnlyList<ActionParam> Params,
    string Description,
    Func<AgentContext, IReadOnlyList<string>, ActionOutcome> Run)
{
    public string Signature => Params.Count == 0 ? Name : $"{Name}({string.Join(", ", Params)})";
}

// The agent's current action set A_t = primitives ∪ helpers ∪ library skills (PLAN §3).
// Every decision, from the CLI or the LLM, goes through Execute so that validation,
// decision accounting and "last action" are identical for all callers.
public sealed class ActionSpace
{
    private readonly List<AgentAction> _actions = new();

    public IReadOnlyList<AgentAction> Actions => _actions;

    public void Register(AgentAction action)
    {
        if (_actions.Any(a => a.Name == action.Name))
            throw new InvalidOperationException($"action '{action.Name}' already registered");
        _actions.Add(action);
    }

    public AgentAction? Get(string name) => _actions.FirstOrDefault(a => a.Name == name);

    public ActionSpace Clone()
    {
        var copy = new ActionSpace();
        copy._actions.AddRange(_actions);
        return copy;
    }

    public ActionOutcome Execute(AgentContext ctx, string name, IReadOnlyList<string> args) =>
        ctx.Decide(c => ExecuteUnrecorded(c, name, args));

    private ActionOutcome ExecuteUnrecorded(AgentContext ctx, string name, IReadOnlyList<string> args)
    {
        var call = args.Count == 0 ? name : $"{name}({string.Join(",", args)})";
        if (ctx.IsOver)
            return new ActionOutcome(call, "invalid", 0, "episode is over");

        var action = Get(name);
        if (action is null)
            return new ActionOutcome(call, "invalid", 0,
                $"unknown action '{name}'; available: {string.Join(", ", _actions.Select(a => a.Name))}");

        var required = action.Params.Count(p => !p.Optional);
        if (args.Count < required || args.Count > action.Params.Count)
            return new ActionOutcome(call, "invalid", 0, $"expected {action.Signature}");

        var resolved = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var error = Validate(ctx, action.Params[i], args[i], out var value);
            if (error is not null)
                return new ActionOutcome(call, "invalid", 0, error);
            resolved.Add(value);
        }
        return action.Run(ctx, resolved);
    }

    // Entity arguments are checked against the agent's memory, not the world: naming an
    // entity the agent has never seen must look exactly like naming one that does not exist.
    private static string? Validate(AgentContext ctx, ActionParam p, string raw, out string value)
    {
        value = raw.Trim();
        var v = value;
        string? KnownOf(EntityKind kind, string label)
        {
            if (ctx.Memory.Get(v) is { } s && s.Kind == kind)
                return null;
            var known = ctx.Memory.Sightings.Values.Where(s => s.Kind == kind).Select(s => s.Id).Order().ToList();
            return $"unknown {label} '{v}' (known: {(known.Count == 0 ? "none" : string.Join(", ", known))})";
        }

        switch (p.Type)
        {
            case ParamType.Direction:
                value = v.ToUpperInvariant();
                return value is "N" or "E" or "S" or "W" ? null : $"{p.Name} must be N, E, S or W";
            case ParamType.Int:
                return int.TryParse(v, out _) ? null : $"{p.Name} must be a whole number";
            case ParamType.Guard:
                return KnownOf(EntityKind.Guard, "guard");
            case ParamType.Door:
                return KnownOf(EntityKind.Door, "door");
            case ParamType.Item:
                return KnownOf(EntityKind.Item, "item");
            case ParamType.ItemOrNone:
                if (v.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    value = "none";
                    return null;
                }
                return KnownOf(EntityKind.Item, "item");
            case ParamType.Target:
                if (v == "exit")
                    return ctx.Memory.ExitPosition is null ? "the exit has not been seen yet" : null;
                return ctx.Memory.Get(v) is null ? $"unknown target '{v}'" : null;
            default:
                return $"unsupported parameter type {p.Type}";
        }
    }
}

public static class StandardActions
{
    // Primitives and built-in helpers: the starting action set for every condition.
    public static ActionSpace Create()
    {
        var space = new ActionSpace();
        foreach (var a in Primitives().Concat(Helpers()))
            space.Register(a);
        return space;
    }

    public static IEnumerable<AgentAction> Primitives()
    {
        yield return Prim("move", [new("dir", ParamType.Direction)],
            "Step one cell N (up), E (right), S (down) or W (left).",
            (w, a) => w.Move(Enum.Parse<Direction>(a[0])));
        yield return Prim("wait", [], "Do nothing for one tick.", (w, _) => w.Wait());
        yield return Prim("look", [], "Spend one tick looking around.", (w, _) => w.Look());
        yield return Prim("pick_up", [], "Pick up the item on your cell.", (w, _) => w.PickUp());
        yield return Prim("talk", [new("guard", ParamType.Guard)],
            "Talk to a guard within 2 cells. May calm it permanently; may fail.",
            (w, a) => w.Talk(a[0]));
        yield return Prim("give", [new("item", ParamType.Item), new("guard", ParamType.Guard)],
            "Give a held item to an adjacent guard. Food may distract it for a few ticks, a coin may bribe it permanently; may be refused.",
            (w, a) => w.Give(a[0], a[1]));
        yield return Prim("throw", [new("item", ParamType.Item), new("x", ParamType.Int), new("y", ParamType.Int)],
            "Throw a held item onto a visible floor cell within 3 cells. Guards within 4 cells of where it lands may be distracted for a few ticks.",
            (w, a) => w.Throw(a[0], new Cell(int.Parse(a[1]), int.Parse(a[2]))));
        yield return Prim("use", [new("item", ParamType.ItemOrNone), new("door", ParamType.Door)],
            "Use a held item on an orthogonally adjacent door, or 'none' to push it. Keys open their own door; lockpicks and pushing may fail.",
            (w, a) => w.Use(a[0] == "none" ? null : a[0], a[1]));
    }

    public static IEnumerable<AgentAction> Helpers()
    {
        yield return new AgentAction("go_to", ActionKind.Helper,
            [new("target", ParamType.Target), new("distance", ParamType.Int, Optional: true)],
            "Walk over known cells to a seen entity or 'exit'. Stops next to doors and guards, on items and the exit, " +
            "or earlier if something new comes into sight, a move fails, or a guard becomes alert.",
            (ctx, a) => ActionOutcome.From(Navigation.GoTo(ctx, a[0], a.Count > 1 ? int.Parse(a[1]) : null)));
        yield return new AgentAction("explore", ActionKind.Helper, [],
            "Walk toward the nearest unexplored area until something new comes into sight.",
            (ctx, _) => ActionOutcome.From(Navigation.Explore(ctx)));
    }

    private static AgentAction Prim(string name, ActionParam[] ps, string description,
        Func<DungeonWorld, IReadOnlyList<string>, ActionResult> run) =>
        new(name, ActionKind.Primitive, ps, description, (ctx, a) =>
        {
            var before = ctx.Tick;
            var r = ctx.Do(w => run(w, a));
            return ActionOutcome.From(r, (int)(ctx.Tick - before));
        });
}
