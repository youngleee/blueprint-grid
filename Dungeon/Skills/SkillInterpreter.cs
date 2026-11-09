using Dungeon.Agent;
using Dungeon.Core;

namespace Dungeon.Skills;

public sealed record SkillLimits(int MaxSteps = 40, int MaxTicks = 120, int MaxDepth = 3);

// Runs a skill body. Closed-loop by construction: every condition is evaluated on a fresh
// FactSet built after the previous step, so the same skill takes different branches when the
// world responds differently (PLAN §3 property 2).
public sealed class SkillInterpreter
{
    private readonly AgentContext _ctx;
    private readonly ActionSpace _space;
    private readonly SkillLimits _limits;
    private readonly long _startTick;
    private readonly List<string> _steps = new();
    private int _calls;

    private SkillInterpreter(AgentContext ctx, ActionSpace space, SkillLimits limits)
    {
        _ctx = ctx;
        _space = space;
        _limits = limits;
        _startTick = ctx.Tick;
    }

    public static ActionOutcome Run(Skill skill, AgentContext ctx, ActionSpace space, IReadOnlyList<string> args,
        SkillLimits? limits = null)
    {
        var call = args.Count == 0 ? skill.Name : $"{skill.Name}({string.Join(",", args)})";
        var l = limits ?? new SkillLimits();
        if (ctx.SkillDepth >= l.MaxDepth)
            return new ActionOutcome(call, "invalid", 0, $"skill nesting deeper than {l.MaxDepth}");

        var bindings = new Dictionary<string, string>();
        for (var i = 0; i < skill.Params.Count; i++)
            bindings[skill.Params[i].Name] = args[i];

        var unmet = skill.Pre.FirstOrDefault(c => !c.Holds(FactSet.From(ctx), bindings));
        if (unmet is not null)
            return new ActionOutcome(call, "invalid", 0, $"precondition not met: {unmet.Text}");

        var interpreter = new SkillInterpreter(ctx, space, l);
        ctx.SkillDepth++;
        Outcome result;
        try
        {
            result = interpreter.Block(skill.Body, bindings);
        }
        finally
        {
            ctx.SkillDepth--;
        }

        var ticks = (int)(ctx.Tick - interpreter._startTick);
        var steps = interpreter._steps;
        if (result.Kind == OutcomeKind.Stop)
            return new ActionOutcome(call, result.Status!, ticks, result.Message!) { Steps = steps };

        // Contract check (PLAN §6.5): the declared effect is what "success" means.
        var facts = FactSet.From(ctx);
        var missed = skill.Effect.FirstOrDefault(c => !c.Holds(facts, bindings));
        return missed is null
            ? new ActionOutcome(call, "success", ticks, Summary(steps)) { Steps = steps }
            : new ActionOutcome(call, "failed", ticks, $"effect not reached: {missed.Text}") { Steps = steps };
    }

    private enum OutcomeKind { Continue, Stop }

    private readonly record struct Outcome(OutcomeKind Kind, string? Status = null, string? Message = null)
    {
        public static readonly Outcome Continue = new(OutcomeKind.Continue);
        public static Outcome Stop(string status, string message) => new(OutcomeKind.Stop, status, message);
    }

    private Outcome Block(IReadOnlyList<SkillStmt> block, IReadOnlyDictionary<string, string> b)
    {
        foreach (var stmt in block)
        {
            var o = Statement(stmt, b);
            if (o.Kind == OutcomeKind.Stop)
                return o;
        }
        return Outcome.Continue;
    }

    private Outcome Statement(SkillStmt stmt, IReadOnlyDictionary<string, string> b)
    {
        if (_ctx.IsOver)
            return Outcome.Stop("episode_over", $"episode {_ctx.World.Status.ToString().ToLowerInvariant()}: {_ctx.World.FailureReason ?? "exit reached"}");

        switch (stmt)
        {
            case CallStmt c:
                return Call(c, b);

            case IfStmt i:
                return Block(i.Condition.Holds(FactSet.From(_ctx), b) ? i.Then : i.Else, b);

            case UntilStmt u:
                for (var pass = 0; pass < u.Max; pass++)
                {
                    if (u.Condition.Holds(FactSet.From(_ctx), b))
                        return Outcome.Continue;
                    var o = Block(u.Body, b);
                    if (o.Kind == OutcomeKind.Stop)
                        return o;
                }
                // Not reaching the condition is not an error by itself; the effect check decides.
                return Outcome.Continue;

            case RepeatStmt r:
                for (var pass = 0; pass < r.Count; pass++)
                {
                    var o = Block(r.Body, b);
                    if (o.Kind == OutcomeKind.Stop)
                        return o;
                }
                return Outcome.Continue;

            case FailStmt f:
                return Outcome.Stop("failed", f.Reason);

            default:
                return Outcome.Stop("invalid", $"unsupported statement {stmt.GetType().Name}");
        }
    }

    private Outcome Call(CallStmt c, IReadOnlyDictionary<string, string> b)
    {
        if (++_calls > _limits.MaxSteps)
            return Outcome.Stop("failed", $"step limit of {_limits.MaxSteps} reached");
        if (_ctx.Tick - _startTick >= _limits.MaxTicks)
            return Outcome.Stop("failed", $"tick limit of {_limits.MaxTicks} reached");

        var args = new List<string>();
        foreach (var a in c.Args)
        {
            var error = Resolve(a, b, args);
            if (error is not null)
            {
                _steps.Add($"{c.Action}(…) -> invalid: {error}");
                return Outcome.Stop("failed", error);
            }
        }

        var outcome = _space.Invoke(_ctx, c.Action, args);
        _steps.Add(outcome.ToString());
        _steps.AddRange(outcome.Steps.Select(s => "  " + s));

        // An invalid step means the skill is wrong for this situation; stop instead of guessing.
        if (outcome.IsInvalid)
            return Outcome.Stop("failed", $"step {outcome.Call} was invalid: {outcome.Message}");
        // A called skill that failed its own contract fails the caller too: otherwise an inner
        // failure would be hidden behind an outer skill with a weak effect.
        if (_space.Get(c.Action)?.Kind == ActionKind.Skill && outcome.Status != "success")
            return Outcome.Stop(outcome.Status == "episode_over" ? "episode_over" : "failed",
                $"step {outcome.Call} {outcome.Status}: {outcome.Message}");
        return Outcome.Continue;
    }

    // Appends the resolved value(s) of one argument expression to `into`.
    private string? Resolve(ArgExpr arg, IReadOnlyDictionary<string, string> b, List<string> into)
    {
        string Bound(string name) => b.TryGetValue(name, out var v) ? v : name;
        switch (arg)
        {
            case NameArg n:
                into.Add(Bound(n.Name));
                return null;
            case FuncArg { Function: "pos" } f:
            {
                var id = Bound(f.Args[0]);
                if (_ctx.KnownPosition(id) is not { } p)
                    return $"pos({id}): position unknown";
                into.Add(p.X.ToString());
                into.Add(p.Y.ToString());
                return null;
            }
            case FuncArg { Function: "held" } f:
            {
                var what = Bound(f.Args[0]);
                var item = FactSet.From(_ctx).Inventory.FirstOrDefault(s => s.Id == what || s.Subtype == what);
                if (item is null)
                    return $"held({what}): not holding one";
                into.Add(item.Id);
                return null;
            }
            case FuncArg { Function: "key_for" } f:
            {
                var door = Bound(f.Args[0]);
                var key = FactSet.From(_ctx).Inventory.FirstOrDefault(s => s.Opens == door);
                if (key is null)
                    return $"key_for({door}): no key for it";
                into.Add(key.Id);
                return null;
            }
            default:
                return $"unsupported argument {arg}";
        }
    }

    private static string Summary(IReadOnlyList<string> steps)
    {
        var top = steps.Where(s => !s.StartsWith(' ')).ToList();
        return top.Count switch
        {
            0 => "no steps",
            1 => top[0],
            _ => $"{top.Count} steps, last: {top[^1]}"
        };
    }
}
