using Dungeon.Core;

namespace Dungeon.Agent;

public sealed record EntityFact(EntitySighting Sighting, bool InSight, int? Distance, bool Adjacent);

// Snapshot of everything the agent knows, at one moment. This is the LLM's only view of the world.
public sealed record FactSet(
    long Tick,
    Cell Position,
    Cell? Exit,
    bool ExitInSight,
    IReadOnlyList<EntitySighting> Inventory,
    IReadOnlyList<EntityFact> Entities,
    int FrontierCells,
    int? NearestFrontierDistance,
    string? LastOutcome)
{
    public static FactSet From(AgentContext ctx)
    {
        var pos = ctx.Position;
        var inventory = ctx.Memory.Sightings.Values
            .Where(s => s.State == "held")
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
        var entities = ctx.Memory.Sightings.Values
            .Where(s => s.Position is not null)
            .Select(s => new EntityFact(s,
                InSight: ctx.World.IsVisible(s.Position!.Value),
                Distance: pos.Chebyshev(s.Position.Value),
                Adjacent: pos.Manhattan(s.Position.Value) == 1))
            .OrderBy(e => e.InSight ? 0 : 1).ThenBy(e => e.Distance).ThenBy(e => e.Sighting.Id, StringComparer.Ordinal)
            .ToList();
        var frontier = Navigation.FrontierCells(ctx).ToList();
        int? nearest = frontier.Count == 0 ? null : Navigation.PathDistance(ctx, frontier);

        return new FactSet(ctx.Tick, pos, ctx.Memory.ExitPosition,
            ctx.Memory.ExitPosition is { } exit && ctx.World.IsVisible(exit),
            inventory, entities, frontier.Count, nearest, ctx.Memory.LastOutcome);
    }

    public string ToText()
    {
        var lines = new List<string>
        {
            $"tick {Tick}, position {Position}, exit {(Exit is null ? "not found yet" : $"at {Exit}, distance {Position.Chebyshev(Exit.Value)}{(ExitInSight ? ", in sight" : "")}")}",
            $"inventory: {(Inventory.Count == 0 ? "empty" : string.Join(", ", Inventory.Select(Describe)))}"
        };

        var inSight = Entities.Where(e => e.InSight).ToList();
        lines.Add("in sight:" + (inSight.Count == 0 ? " nothing" : ""));
        lines.AddRange(inSight.Select(e => "  " + Describe(e)));

        var remembered = Entities.Where(e => !e.InSight).ToList();
        if (remembered.Count > 0)
        {
            lines.Add("remembered (out of sight):");
            lines.AddRange(remembered.Select(e => $"  {Describe(e)}, last seen tick {e.Sighting.LastSeenTick}"));
        }

        lines.Add(FrontierCells == 0
            ? "unexplored frontier: none reachable"
            : $"unexplored frontier: {FrontierCells} cells, nearest reachable at distance {NearestFrontierDistance?.ToString() ?? "?"}");
        lines.Add($"last action: {(LastOutcome ?? "none")}");
        return string.Join('\n', lines);
    }

    private static string Describe(EntitySighting s) =>
        s.Opens is null ? $"{s.Id} ({s.Subtype})" : $"{s.Id} ({s.Subtype}, opens {s.Opens})";

    private static string Describe(EntityFact e)
    {
        var s = e.Sighting;
        var what = s.Kind switch
        {
            EntityKind.Guard => $"{s.Id} guard at {s.Position}, {s.State}",
            EntityKind.Door => $"{s.Id} {s.Subtype} door at {s.Position}, {s.State}",
            _ => $"{s.Id} {s.Subtype}{(s.Opens is null ? "" : $" (opens {s.Opens})")} on floor at {s.Position}"
        };
        return $"{what}, distance {e.Distance}{(e.Adjacent ? ", adjacent" : "")}";
    }
}
