using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Skills;

// Defaults calibrated in week 7 (PLAN §6.4): 20 rooms, ≥ 16 passes and no lost episode. Over 40
// disjoint seed blocks this accepted the two good calm_guard drafts in 39/40 and 37/40 blocks and
// the weak draft in 4/40; a pass-rate threshold alone accepted the weak draft in 29/40.
public sealed record ScenarioTestOptions(
    int Count = 20,
    int Required = 16,
    int MaxLost = 0,
    int MaxAttempts = 400,
    int SeedBase = 1_000_000,
    int SightRadius = DungeonWorld.DefaultSightRadius);

public sealed record ScenarioCase(int Seed, string Setup, string Outcome, bool EffectReached, bool Survived, int Ticks)
{
    public bool Passed => EffectReached && Survived;
}

public sealed record ScenarioReport(string Skill, IReadOnlyList<ScenarioCase> Cases, int Required, int MaxLost, int Attempts, string? Error)
{
    public int Passed => Cases.Count(c => c.Passed);
    public int Lost => Cases.Count(c => !c.Survived);
    public double Rate => Cases.Count == 0 ? 0 : Passed / (double)Cases.Count;

    // Losing the episode is categorically worse than not reaching the effect: a skill that sometimes
    // gets the agent caught is rejected however often it succeeds otherwise.
    public bool Accepted => Error is null && Passed >= Required && Lost <= MaxLost;

    public override string ToString() => Error is not null
        ? $"{Skill}: rejected — {Error}"
        : $"{Skill}: {(Accepted ? "accepted" : "rejected")} — effect reached safely in {Passed}/{Cases.Count} test rooms (need {Required})" +
          (Lost > 0 ? $", episode lost in {Lost} (allowed {MaxLost})" : "");
}

// Verifies a skill in freshly generated micro-rooms, never in a copy of the real episode, so
// verification cannot leak unseen rooms or future random outcomes (PLAN §6.4 step 2).
//
// Each room: start room — 5-cell corridor — exit room. One entity per skill parameter sits in
// the corridor, the first at its entrance. The agent starts in sight of it, at a distance drawn
// to match first sightings in real episodes. Entity traits come from the same ranges as the real
// dungeons; the agent holds a random subset of items. A room is used only if the skill's
// precondition holds and its effect does not hold yet (otherwise the test proves nothing).
// Test seeds are the same for every skill, so pass rates of different skills are paired.
//
// Macros (ablation E) have no parameters but name entities directly ("guard_3"). They are tested in
// the same rooms, with each named guard / door placed under its own id instead of a parameter
// slot, so they face exactly the same situations as a closed-loop skill for that kind of obstacle.
public static class ScenarioTester
{
    private const int Width = 19, Height = 7, CorridorY = 3, CorridorStart = 7, CorridorEnd = 11;

    public static ScenarioReport Test(Skill skill, ActionSpace space, ScenarioTestOptions? options = null,
        ScenarioOptions? traits = null)
    {
        var o = options ?? new ScenarioTestOptions();
        var t = traits ?? new ScenarioOptions();
        var testSpace = space.Clone();
        if (testSpace.Get(skill.Name) is null)
            testSpace.Register(SkillActions.ToAction(skill));

        var slots = Slots(skill);
        if (slots.Count(s => s.Type is ParamType.Guard or ParamType.Door or ParamType.Target) > CorridorEnd - CorridorStart + 1)
            return new ScenarioReport(skill.Name, [], o.Required, o.MaxLost, 0, "too many entity parameters for a test room");

        var cases = new List<ScenarioCase>();
        int attempts = 0, preFailed = 0, alreadyDone = 0;
        while (cases.Count < o.Count && attempts < o.MaxAttempts)
        {
            var seed = o.SeedBase + attempts++;
            var (world, values, setup) = Build(slots, seed, o, t);
            var args = values.Take(skill.Params.Count).ToList();
            var ctx = new AgentContext(world);
            var facts = FactSet.From(ctx);
            var bindings = skill.Params.Select((p, i) => (p.Name, args[i])).ToDictionary(x => x.Name, x => x.Item2);
            if (!skill.Pre.All(c => c.Holds(facts, bindings))) { preFailed++; continue; }
            if (skill.Effect.All(c => c.Holds(facts, bindings))) { alreadyDone++; continue; }

            var outcome = testSpace.Execute(ctx, skill.Name, args);
            var after = FactSet.From(ctx);
            var effect = skill.Effect.All(c => c.Holds(after, bindings));
            var survived = ctx.World.Status != EpisodeStatus.Failed;
            cases.Add(new ScenarioCase(seed, setup, outcome.ToString(), effect, survived, outcome.Ticks));
        }

        string? error = null;
        if (cases.Count < o.Count)
            error = cases.Count == 0
                ? $"no test room satisfied the precondition (tried {attempts}: precondition false {preFailed}, effect already true {alreadyDone})"
                : $"only {cases.Count} of {o.Count} test rooms satisfied the precondition";
        return new ScenarioReport(skill.Name, cases, o.Required, o.MaxLost, attempts, error);
    }

    private sealed record Slot(ParamType Type, string? FixedId);

    // One slot per parameter; for a parameterless skill, one per guard / door id it names.
    private static List<Slot> Slots(Skill skill)
    {
        if (skill.Params.Count > 0)
            return skill.Params.Select(p => new Slot(p.Type, null)).ToList();
        var json = SkillJson.Serialize(skill, indented: false);
        return System.Text.RegularExpressions.Regex.Matches(json, @"\b(guard|door)_\d+\b")
            .Select(m => m.Value).Distinct()
            .Select(id => new Slot(id.StartsWith("guard") ? ParamType.Guard : ParamType.Door, id))
            .ToList();
    }

    private static (DungeonWorld World, List<string> Args, string Setup) Build(IReadOnlyList<Slot> slots, int seed,
        ScenarioTestOptions o, ScenarioOptions t)
    {
        var rng = Rng.ForStream(seed, "skilltest");
        var tiles = new Tile[Width, Height];
        Carve(tiles, 1, 1, 6, 5);
        Carve(tiles, CorridorStart, CorridorY, CorridorEnd, CorridorY);
        Carve(tiles, CorridorEnd + 1, 1, Width - 2, 5);

        var doors = new List<Door>();
        var guards = new List<Guard>();
        var items = new List<Item>();
        var args = new List<string>();
        var setup = new List<string>();
        var slot = CorridorStart;
        var itemCount = 0;

        foreach (var p in slots)
        {
            switch (p.Type)
            {
                case ParamType.Guard or ParamType.Target:
                {
                    var id = p.FixedId ?? $"guard_{guards.Count + 1}";
                    guards.Add(new Guard(id, new Cell(slot++, CorridorY), new GuardTraits(
                        rng.Range(t.CalmMin, t.CalmMax), rng.Range(t.FoodMin, t.FoodMax),
                        rng.Range(t.CoinMin, t.CoinMax), rng.Range(t.ThrowMin, t.ThrowMax),
                        t.DistractMinTicks, t.DistractMaxTicks, rng.Next(t.MinPatience, t.MaxPatience + 1))));
                    args.Add(id);
                    setup.Add(id);
                    break;
                }
                case ParamType.Door:
                {
                    var id = p.FixedId ?? $"door_{doors.Count + 1}";
                    var locked = rng.Chance(0.5);
                    doors.Add(new Door(id, new Cell(slot++, CorridorY), locked ? DoorKind.Locked : DoorKind.Jammed,
                        locked ? rng.Range(t.PickMin, t.PickMax) : 0, locked ? 0 : rng.Range(t.PushMin, t.PushMax)));
                    if (locked && rng.Chance(0.5))
                        items.Add(new Item($"key_{++itemCount}", ItemType.Key, null, opens: id));
                    args.Add(id);
                    setup.Add($"{id} {(locked ? "locked" : "jammed")}");
                    break;
                }
                case ParamType.Item:
                {
                    var type = new[] { ItemType.Food, ItemType.Coin, ItemType.Lockpick }[rng.Next(0, 3)];
                    var id = $"{type.ToString().ToLowerInvariant()}_{++itemCount}";
                    items.Add(new Item(id, type, null));
                    args.Add(id);
                    break;
                }
                case ParamType.Int:
                    args.Add(rng.Next(1, 4).ToString());
                    break;
                case ParamType.Direction:
                    args.Add("NESW"[rng.Next(0, 4)].ToString());
                    break;
                default:
                    args.Add("none");
                    break;
            }
        }

        foreach (var type in new[] { ItemType.Food, ItemType.Coin, ItemType.Lockpick })
            if (rng.Chance(0.5))
                items.Add(new Item($"{type.ToString().ToLowerInvariant()}_{++itemCount}", type, null));

        // Start distance: first sightings in real tier-3 episodes are mostly at the sight radius.
        var roll = rng.NextDouble();
        var distance = roll < 0.8 ? 3 : roll < 0.93 ? 2 : 1;
        var start = new Cell(CorridorStart - distance, CorridorY + rng.Next(-Math.Min(distance, 2), Math.Min(distance, 2) + 1));
        var map = new DungeonMap(tiles, [new Room(1, 1, 6, 5), new Room(CorridorEnd + 1, 1, Width - CorridorEnd - 2, 5)],
            start, new Cell(Width - 3, CorridorY));

        setup.Add($"agent {distance} away");
        setup.Add(items.Count == 0 ? "holding nothing" : $"holding {string.Join(", ", items.Select(i => i.Type.ToString().ToLowerInvariant()))}");
        return (new DungeonWorld(new Scenario(map, doors, guards, items), seed, o.SightRadius), args, string.Join(", ", setup));
    }

    private static void Carve(Tile[,] tiles, int x0, int y0, int x1, int y1)
    {
        for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
                tiles[x, y] = Tile.Floor;
    }
}

public sealed record AdmissionResult(Skill Skill, ValidationResult Validation, ScenarioReport? Tests)
{
    public bool Accepted => Validation.Ok && Tests is { Accepted: true };

    // Message for the LLM and for humans: what to fix, with locations and test evidence.
    public string Feedback()
    {
        if (!Validation.Ok)
            return "The skill was rejected by the validator:\n" + string.Join("\n", Validation.Errors.Select(e => "- " + e));
        if (Tests is null)
            return "not tested";
        if (Tests.Error is not null)
            return $"The skill was rejected in testing: {Tests.Error}.";
        var lines = new List<string> { Tests + ":" };
        lines.AddRange(Tests.Cases.Select(c =>
            $"- {(c.Passed ? "pass" : c.Survived ? "fail" : "fail (episode lost)")}: {c.Setup} → {c.Outcome}"));
        return string.Join("\n", lines);
    }
}

public static class SkillAdmission
{
    public static AdmissionResult Check(Skill skill, ActionSpace space, IEnumerable<Skill>? existing = null,
        ValidationOptions? validation = null, ScenarioTestOptions? tests = null)
    {
        var v = SkillValidator.Validate(skill, space, existing, validation);
        return new AdmissionResult(skill, v, v.Ok ? ScenarioTester.Test(skill, space, tests) : null);
    }
}
