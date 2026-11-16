using System.Text.Json;
using Dungeon.Core;
using Dungeon.Skills;

namespace Dungeon.Agent;

public sealed record ReplayFrame(int Decision, string Action, string Reason, string Outcome, string? Learned, DungeonWorld World);

public sealed record ReplayResult(IReadOnlyList<ReplayFrame> Frames, EpisodeStatus Status, long Ticks, bool MatchesTrace, string? Mismatch);

// Re-executes a recorded episode from its trace: same seed, same starting library, same decisions,
// and skills registered at the moment they were accepted. Because the world is deterministic given
// the seed and the action sequence, the replay must reproduce every recorded outcome; a mismatch
// means the code changed since the run (or the trace was edited). No LLM is involved.
public static class EpisodeReplay
{
    public static ReplayResult Replay(string runDir, int seed)
    {
        using var runDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "run.json")));
        var run = runDoc.RootElement;
        var condition = run.GetProperty("condition").GetString()!;
        var tier = run.GetProperty("tier").GetString()!;
        var source = run.TryGetProperty("library", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;

        var library = source switch
        {
            null or "none" => new List<Skill>(),
            "handwritten" => HandwrittenSkills.Load().ToList(),
            _ => SkillJson.LoadLibrary(File.ReadAllText(source), StandardActions.Create())
        };
        // C / E: skills learned in earlier episodes of this run are part of this episode's start.
        if (condition is "C" or "E")
            library.AddRange(LearnedBefore(runDir, seed, run));
        var space = condition is "A" or "R" ? StandardActions.Create() : StandardActions.Create().WithSkills(library);

        var tracePath = Path.Combine(runDir, "traces", $"seed_{seed}.jsonl");
        if (!File.Exists(tracePath))
            throw new FileNotFoundException($"no trace for seed {seed} in {runDir}");

        var world = new DungeonWorld(ScenarioGenerator.Generate(seed, ExperimentRunner.TierOptions(tier)), seed);
        var ctx = new AgentContext(world);
        var frames = new List<ReplayFrame>();
        string? mismatch = null;

        foreach (var line in File.ReadLines(tracePath))
        {
            using var stepDoc = JsonDocument.Parse(line);
            var step = stepDoc.RootElement;
            string? learned = null;
            if (step.TryGetProperty("induction", out var induction))
                foreach (var attempt in induction.EnumerateArray())
                    if (attempt.GetProperty("accepted").GetBoolean()
                        && SkillJson.TryParse(Llm.JsonText.StripFences(attempt.GetProperty("raw_reply").GetString()!), out var skill, out _))
                    {
                        space.Register(SkillActions.ToAction(skill!));
                        learned = skill!.Name;
                    }

            var action = step.GetProperty("action").GetString()!;
            var args = step.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToList();
            var recorded = step.GetProperty("outcome").GetString()!;
            var outcome = step.TryGetProperty("parse_error", out _)
                ? recorded
                : space.Execute(ctx, action, args).ToString();
            if (mismatch is null && outcome != recorded)
                mismatch = $"decision {step.GetProperty("decision").GetInt32()}: recorded '{recorded}', replayed '{outcome}'";

            frames.Add(new ReplayFrame(step.GetProperty("decision").GetInt32(), action,
                step.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "", outcome, learned, world.Clone()));
        }
        return new ReplayResult(frames, world.Status, world.Tick, mismatch is null, mismatch);
    }

    private static IEnumerable<Skill> LearnedBefore(string runDir, int seed, JsonElement run)
    {
        var path = Path.Combine(runDir, "induction.jsonl");
        if (!File.Exists(path))
            yield break;
        var order = run.GetProperty("seeds").EnumerateArray().Select(s => s.GetInt32()).ToList();
        var position = order.IndexOf(seed);
        foreach (var line in File.ReadLines(path))
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (order.IndexOf(e.GetProperty("seed").GetInt32()) >= position)
                continue;
            foreach (var a in e.GetProperty("attempts").EnumerateArray())
                if (a.GetProperty("accepted").GetBoolean()
                    && SkillJson.TryParse(Llm.JsonText.StripFences(a.GetProperty("raw_reply").GetString()!), out var skill, out _))
                    yield return skill!;
        }
    }
}
