using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Agent;

public enum HelperStatus { Arrived, Interrupted, Unreachable, EpisodeOver }

public sealed record HelperResult(string Action, HelperStatus Status, int Ticks, string Message)
{
    public override string ToString() =>
        $"{Action} -> {Status.ToString().ToLowerInvariant()} after {Ticks} ticks: {Message}";
}

// Built-in navigation (PLAN §6.2). Plans only over what the agent knows, re-plans every step,
// and hands control back as soon as something decision-relevant happens.
public static class Navigation
{
    public const int DefaultMaxTicks = 80;

    // go_to: walk until within Manhattan `stopDistance` of `target` (0 = stand on it).
    public static HelperResult GoTo(AgentContext ctx, Cell target, int stopDistance, string label, int maxTicks = DefaultMaxTicks)
    {
        var name = $"go_to({label})";
        var ticks = 0;
        while (true)
        {
            if (ctx.IsOver)
                return new(name, HelperStatus.EpisodeOver, ticks, $"episode {ctx.World.Status.ToString().ToLowerInvariant()}");
            if (ctx.Position.Manhattan(target) <= stopDistance)
                return new(name, HelperStatus.Arrived, ticks, $"at {ctx.Position}");
            if (ticks >= maxTicks)
                return new(name, HelperStatus.Interrupted, ticks, "tick limit reached");

            var goals = GoalCells(ctx, target, stopDistance);
            var step = FirstStep(ctx, goals);
            if (step is null)
                return new(name, HelperStatus.Unreachable, ticks, $"no known path to {target}");

            var interrupt = StepAndCheck(ctx, step.Value, ref ticks);
            if (interrupt is not null)
                return ctx.Position.Manhattan(target) <= stopDistance
                    ? new(name, HelperStatus.Arrived, ticks, $"at {ctx.Position}; {interrupt}")
                    : new(name, HelperStatus.Interrupted, ticks, interrupt);
        }
    }

    public static HelperResult GoTo(AgentContext ctx, string id, int? stopDistance = null, int maxTicks = DefaultMaxTicks)
    {
        var target = ctx.KnownPosition(id);
        if (target is null)
            return new($"go_to({id})", HelperStatus.Unreachable, 0, $"{id} is not known");
        var kind = ctx.Memory.Get(id)?.Kind;
        var d = stopDistance ?? (kind is EntityKind.Guard or EntityKind.Door ? 1 : 0);
        return GoTo(ctx, target.Value, d, d == 0 ? id : $"{id},{d}", maxTicks);
    }

    // retreat: walk away from a remembered entity until at least `distance` cells (Chebyshev) from it.
    // Steps to the reachable known cell that maximises distance, so it never walks past the entity.
    public static HelperResult Retreat(AgentContext ctx, string id, int distance, int maxTicks = DefaultMaxTicks)
    {
        var name = $"retreat({id},{distance})";
        if (ctx.KnownPosition(id) is not { } from)
            return new(name, HelperStatus.Unreachable, 0, $"{id} is not known");
        var ticks = 0;
        while (true)
        {
            if (ctx.IsOver)
                return new(name, HelperStatus.EpisodeOver, ticks, $"episode {ctx.World.Status.ToString().ToLowerInvariant()}");
            if (ctx.Position.Chebyshev(from) >= distance)
                return new(name, HelperStatus.Arrived, ticks, $"at {ctx.Position}, distance {ctx.Position.Chebyshev(from)} from {id}");
            if (ticks >= maxTicks)
                return new(name, HelperStatus.Interrupted, ticks, "tick limit reached");

            var goals = ReachableKnown(ctx).Where(c => c.Chebyshev(from) >= distance).ToHashSet();
            var step = FirstStep(ctx, goals);
            if (step is null)
                return new(name, HelperStatus.Unreachable, ticks, $"no known cell {distance} away from {id}");
            var result = ctx.Do(w => w.Move(step.Value.Direction));
            ticks++;
            if (result.Status != ActionStatus.Success)
                return new(name, HelperStatus.Interrupted, ticks, $"blocked: {result.Message}");
        }
    }

    private static IEnumerable<Cell> ReachableKnown(AgentContext ctx)
    {
        var seen = new HashSet<Cell> { ctx.Position };
        var queue = new Queue<Cell>();
        queue.Enqueue(ctx.Position);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            yield return c;
            foreach (var d in Enum.GetValues<Direction>())
            {
                var n = c.Step(d);
                if (BelievedPassable(ctx, n) && seen.Add(n))
                    queue.Enqueue(n);
            }
        }
    }

    // explore: repeatedly walk to the nearest frontier until something new is seen.
    public static HelperResult Explore(AgentContext ctx, int maxTicks = DefaultMaxTicks)
    {
        const string name = "explore";
        var ticks = 0;
        while (true)
        {
            if (ctx.IsOver)
                return new(name, HelperStatus.EpisodeOver, ticks, $"episode {ctx.World.Status.ToString().ToLowerInvariant()}");
            if (ticks >= maxTicks)
                return new(name, HelperStatus.Interrupted, ticks, "tick limit reached");

            var frontier = FrontierCells(ctx).ToHashSet();
            if (frontier.Count == 0)
                return new(name, HelperStatus.Unreachable, ticks, "nothing left to explore behind known passable cells");
            var step = FirstStep(ctx, frontier);
            if (step is null)
                return new(name, HelperStatus.Unreachable, ticks, "no reachable frontier");

            var interrupt = StepAndCheck(ctx, step.Value, ref ticks);
            if (interrupt is not null)
                return new(name, HelperStatus.Interrupted, ticks, interrupt);
        }
    }

    // Known floor cells the agent believes it can stand on, that border unknown cells.
    public static IEnumerable<Cell> FrontierCells(AgentContext ctx)
    {
        foreach (var c in ctx.World.KnownCells())
        {
            if (!BelievedPassable(ctx, c))
                continue;
            foreach (var d in Enum.GetValues<Direction>())
            {
                var n = c.Step(d);
                if (ctx.World.Map.InBounds(n) && !ctx.World.IsKnown(n))
                {
                    yield return c;
                    break;
                }
            }
        }
    }

    // Shortest known-path length from the agent to any goal cell, or null.
    public static int? PathDistance(AgentContext ctx, IEnumerable<Cell> goals) =>
        FirstStep(ctx, goals.ToHashSet()) is { } s ? s.Length : null;

    // Belief, not truth: known floor, not a remembered closed door, not a remembered blocking guard.
    public static bool BelievedPassable(AgentContext ctx, Cell c)
    {
        if (!ctx.World.IsKnown(c) || !ctx.World.Map.IsFloor(c))
            return false;
        foreach (var s in ctx.Memory.Sightings.Values)
        {
            if (s.Position != c)
                continue;
            if (s.Kind == EntityKind.Door && s.State == "closed")
                return false;
            if (s.Kind == EntityKind.Guard && s.State is "idle" or "alert" or "hostile")
                return false;
        }
        return true;
    }

    private static HashSet<Cell> GoalCells(AgentContext ctx, Cell target, int stopDistance)
    {
        var goals = new HashSet<Cell>();
        for (var dx = -stopDistance; dx <= stopDistance; dx++)
            for (var dy = -stopDistance; dy <= stopDistance; dy++)
            {
                var c = new Cell(target.X + dx, target.Y + dy);
                if (c.Manhattan(target) <= stopDistance && BelievedPassable(ctx, c))
                    goals.Add(c);
            }
        return goals;
    }

    private readonly record struct Step(Direction Direction, int Length);

    // BFS over believed-passable cells; uniform step cost makes BFS optimal.
    private static Step? FirstStep(AgentContext ctx, HashSet<Cell> goals)
    {
        var start = ctx.Position;
        if (goals.Contains(start))
            return null;
        var first = new Dictionary<Cell, Step> { [start] = default };
        var queue = new Queue<Cell>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var d in Enum.GetValues<Direction>())
            {
                var n = c.Step(d);
                if (first.ContainsKey(n) || !BelievedPassable(ctx, n))
                    continue;
                var step = c == start ? new Step(d, 1) : first[c] with { Length = first[c].Length + 1 };
                if (goals.Contains(n))
                    return step;
                first[n] = step;
                queue.Enqueue(n);
            }
        }
        return null;
    }

    // Takes one step; returns an interrupt reason if control should go back to the decision-maker.
    private static string? StepAndCheck(AgentContext ctx, Step step, ref int ticks)
    {
        var exitKnown = ctx.Memory.ExitPosition is not null;
        var guardStates = VisibleGuardStates(ctx);

        var result = ctx.Do(w => w.Move(step.Direction));
        ticks++;

        if (ctx.IsOver)
            return $"episode {ctx.World.Status.ToString().ToLowerInvariant()}";
        if (result.Status != ActionStatus.Success)
            return $"blocked: {result.Message}";
        if (ctx.NewlySeen.Count > 0)
            return $"spotted {string.Join(", ", ctx.NewlySeen)}";
        if (!exitKnown && ctx.Memory.ExitPosition is not null)
            return $"spotted exit at {ctx.Memory.ExitPosition}";
        foreach (var (id, state) in VisibleGuardStates(ctx))
            if (state == GuardState.Alert && guardStates.GetValueOrDefault(id) != GuardState.Alert)
                return $"{id} became alert";
        return null;
    }

    private static Dictionary<string, GuardState> VisibleGuardStates(AgentContext ctx) =>
        ctx.World.Guards.Where(g => ctx.World.IsVisible(g.Position)).ToDictionary(g => g.Id, g => g.State);
}
