using Dungeon.Llm;

namespace Dungeon.Agent;

public sealed record DecisionRecord(
    Decision Decision,
    string? RawReply,
    int InputTokens,
    int OutputTokens,
    bool FromCache,
    string? CacheKey,
    string? ParseError);

// Chooses the next action. Decisions are made only from facts the agent has, never from the world.
public interface IDecider
{
    string Name { get; }
    Task<DecisionRecord> DecideAsync(FactSet facts, ActionSpace space, IReadOnlyList<string> history, int decisionsLeft,
        CancellationToken ct = default);
}

public sealed class LlmDecider(ILlmClient client, string model, double temperature) : IDecider
{
    public string Name => $"llm:{model}";

    public async Task<DecisionRecord> DecideAsync(FactSet facts, ActionSpace space, IReadOnlyList<string> history,
        int decisionsLeft, CancellationToken ct = default)
    {
        var request = new LlmRequest(model, temperature, Prompts.ChooseActionType,
            Prompts.ChooseActionSystem(space), Prompts.ChooseActionUser(facts, history, decisionsLeft));
        var r = await client.CompleteAsync(request, ct);
        if (DecisionParser.TryParse(r.Text, out var decision, out var error))
            return new DecisionRecord(decision, r.Text, r.InputTokens, r.OutputTokens, r.FromCache, r.CacheKey, null);
        // Unparseable replies become an invalid action so they cost a decision and show in the next prompt.
        return new DecisionRecord(new Decision("(unparseable)", [], ""), r.Text, r.InputTokens, r.OutputTokens,
            r.FromCache, r.CacheKey, error);
    }
}

// Hand-written, knowledge-only reference policy (condition R). Not an LLM and never learns:
// it shows what fixed rules over the same facts and actions achieve under the same budget.
// If the action space holds calm_guard / open_door (condition RD), the same rules delegate guards
// and doors to those skills instead of single primitives — the only difference between R and RD,
// so their gap measures what the skills add.
public sealed class RuleBasedDecider : IDecider
{
    public string Name => "rules";

    public Task<DecisionRecord> DecideAsync(FactSet f, ActionSpace space, IReadOnlyList<string> history,
        int decisionsLeft, CancellationToken ct = default) =>
        Task.FromResult(new DecisionRecord(Choose(f, space, history), null, 0, 0, false, null, null));

    private static Decision Choose(FactSet f, ActionSpace space, IReadOnlyList<string> history)
    {
        Decision D(string a, string reason, params string[] args) => new(a, args, reason);
        var last = history.Count > 0 ? history[^1] : "";
        var calmSkill = space.Get("calm_guard") is { Kind: ActionKind.Skill };
        var doorSkill = space.Get("open_door") is { Kind: ActionKind.Skill };

        if (f.Exit is not null && !last.StartsWith("go_to(exit) -> unreachable"))
            return D("go_to", "exit known", "exit");

        var inSight = f.Entities.Where(e => e.InSight).ToList();

        var guard = inSight.FirstOrDefault(e => e.Sighting.Kind == EntityKind.Guard && e.Sighting.State is "alert" or "idle");
        if (guard is not null)
        {
            var g = guard.Sighting.Id;
            if (calmSkill)
                return D("calm_guard", "handle guard with skill", g);
            var food = f.Inventory.FirstOrDefault(i => i.Subtype == "food");
            var coin = f.Inventory.FirstOrDefault(i => i.Subtype == "coin");
            if ((coin ?? food) is { } gift)
                return guard.Distance <= 1
                    ? D("give", "hand over item", gift.Id, g)
                    : D("go_to", "approach guard", g);
            if (guard.Distance <= 2)
                return D("talk", "try to calm guard", g);
        }

        var passable = inSight.FirstOrDefault(e => e.Sighting.Kind == EntityKind.Guard && e.Sighting.State is "calm" or "bribed" or "distracted");
        if (passable is not null && !last.StartsWith("explore -> unreachable"))
            return D("explore", "guard is passable");

        var item = f.Entities.FirstOrDefault(e => e.Sighting is { Kind: EntityKind.Item, State: "on_floor" });
        if (item is not null)
            return item.Distance == 0
                ? D("pick_up", "standing on item")
                : D("go_to", "collect item", item.Sighting.Id);

        if (!last.StartsWith("explore -> unreachable"))
            return D("explore", "look for the exit");

        var door = f.Entities.FirstOrDefault(e => e.Sighting is { Kind: EntityKind.Door, State: "closed" });
        if (door is not null)
        {
            var d = door.Sighting.Id;
            if (doorSkill)
                return D("open_door", "open door with skill", d);
            if (!door.Adjacent)
                return D("go_to", "walk to door", d);
            var key = f.Inventory.FirstOrDefault(i => i.Opens == d);
            var pick = f.Inventory.FirstOrDefault(i => i.Subtype == "lockpick");
            var tool = door.Sighting.Subtype == "locked" ? (key ?? pick)?.Id ?? "none" : "none";
            return D("use", "open door", tool, d);
        }

        var anyGuard = f.Entities.FirstOrDefault(e => e.Sighting.Kind == EntityKind.Guard);
        if (anyGuard is not null)
            return D("go_to", "return to guard", anyGuard.Sighting.Id);
        return D("wait", "nothing to do");
    }
}

