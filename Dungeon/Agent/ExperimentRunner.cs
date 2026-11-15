using System.Text.Json;
using Dungeon.Core;
using Dungeon.Llm;
using Dungeon.Skills;

namespace Dungeon.Agent;

public sealed record ExperimentConfig(
    string Condition,
    string Tier,
    IReadOnlyList<int> Seeds,
    int MaxDecisions,
    string OutputDir,
    string Provider,
    string Model,
    double Temperature,
    CacheMode CacheMode,
    string CacheDir,
    LlmBudget Budget,
    IReadOnlyList<Skill>? Library = null,
    string LibrarySource = "handwritten",
    bool Freeze = false,
    int InduceAttempts = 3,
    int InduceCallsPerEpisode = 6,
    ScenarioTestOptions? Tests = null,
    AdmissionMode Admission = AdmissionMode.Tested);

public sealed record ExperimentSummary(
    int Episodes,
    int Successes,
    double SuccessRate,
    double MeanDecisions,
    int LlmCalls,
    int CacheHits,
    long InputTokens,
    long OutputTokens,
    string? AbortedReason,
    int SkillsInduced = 0,
    int InduceCalls = 0,
    int FinalLibrarySize = 0);

// Runs one condition over a list of seeds and writes:
//   <out>/run.json            config, provider (never the key), summary
//   <out>/metrics.csv         one row per episode (EpisodeResult)
//   <out>/traces/seed_N.jsonl one line per decision (StepTrace)
//   <out>/library.json        B / C / E: the skill library after the run (input for the next phase)
//   <out>/induction.jsonl     B / C / E: every induction request with all attempts and feedback
public static class ExperimentRunner
{
    // A  LLM, primitives + helpers         R  rules, same actions
    // D  LLM + hand-written skills         RD rules + same skills
    // B  LLM + induction, library reset every episode
    // C  LLM + induction, library persists across episodes (main condition)
    // E  as C, but induced skills must be macros (ablation, PLAN §7.3)
    public static readonly string[] Conditions = ["A", "R", "D", "RD", "B", "C", "E"];

    public static bool UsesLlm(string condition) => condition is "A" or "D" or "B" or "C" or "E";

    public static bool Induces(string condition) => condition is "B" or "C" or "E";

    public static SkillForm FormFor(string condition) => condition == "E" ? SkillForm.Macro : SkillForm.ClosedLoop;

    // D and RD use the hand-written library unless another fixed library is given (e.g. a skill draft).
    public static ActionSpace InitialSpace(string condition, IReadOnlyList<Skill>? library = null) => condition switch
    {
        "D" or "RD" => StandardActions.Create().WithSkills(library ?? HandwrittenSkills.Load()),
        _ => StandardActions.Create()
    };

    public static ScenarioOptions TierOptions(string tier) => tier switch
    {
        "tier1" or "1" => ScenarioOptions.Tier1,
        "tier2" or "2" => ScenarioOptions.Tier2,
        "tier3" or "3" => ScenarioOptions.Tier3,
        _ => throw new ArgumentException($"unknown tier '{tier}' (use 1, 2 or 3)")
    };

    public static async Task<ExperimentSummary> RunAsync(ExperimentConfig config, IDecider decider,
        Action<string>? log = null, CancellationToken ct = default, SkillInducer? inducer = null)
    {
        if (Induces(config.Condition) && !config.Freeze && inducer is null)
            throw new ArgumentException($"condition {config.Condition} needs an inducer unless the library is frozen");
        if (config.Freeze && !Induces(config.Condition))
            throw new ArgumentException("--freeze only applies to conditions B, C and E");
        if (!Conditions.Contains(config.Condition))
            throw new ArgumentException($"condition '{config.Condition}' is not implemented yet (available: {string.Join(", ", Conditions)})");

        var tierName = config.Tier.StartsWith("tier") ? config.Tier : $"tier{config.Tier}";
        var options = TierOptions(config.Tier);
        Directory.CreateDirectory(Path.Combine(config.OutputDir, "traces"));
        var csvPath = Path.Combine(config.OutputDir, "metrics.csv");
        await File.WriteAllTextAsync(csvPath, EpisodeResult.CsvHeader + "\n", ct);

        var results = new List<EpisodeResult>();
        string? aborted = null;
        // C and E start from the given library (empty if none) and keep everything they learn.
        var persistent = new List<Skill>(Induces(config.Condition) ? config.Library ?? [] : []);
        var policy = Induces(config.Condition) && !config.Freeze
            ? new InductionPolicy(inducer!, new InductionOptions(config.InduceCallsPerEpisode, config.InduceAttempts))
            : null;
        var libraryPath = Path.Combine(config.OutputDir, "library.json");
        var inductionPath = Path.Combine(config.OutputDir, "induction.jsonl");
        if (Induces(config.Condition))
            await File.WriteAllTextAsync(inductionPath, "", ct);

        foreach (var seed in config.Seeds)
        {
            // A, R, D, RD: every episode starts from the same action set.
            // B: induction from an empty library every episode. C, E: the persistent library.
            List<Skill>? episodeLibrary = config.Condition switch
            {
                "B" => [],
                "C" or "E" => persistent,
                _ => null
            };
            var space = episodeLibrary is null
                ? InitialSpace(config.Condition, config.Library)
                : StandardActions.Create().WithSkills(episodeLibrary);
            EpisodeResult result;
            IReadOnlyList<StepTrace> trace;
            try
            {
                (result, trace) = await EpisodeRunner.RunAsync(
                    new EpisodeConfig(seed, options, tierName, config.MaxDecisions), config.Condition, decider, space, ct,
                    policy, episodeLibrary);
            }
            catch (LlmException e)
            {
                aborted = $"seed {seed}: {e.Message}";
                log?.Invoke($"ABORTED {aborted}");
                break;
            }

            results.Add(result);
            await File.AppendAllTextAsync(csvPath, result.ToCsv() + "\n", ct);
            if (Induces(config.Condition))
            {
                var lines = trace.Where(t => t.Induction is not null).Select(t => JsonSerializer.Serialize(new
                {
                    seed, decision = t.Decision, tick = t.TickBefore, attempts = t.Induction
                }, EpisodeRunner.TraceJson));
                await File.AppendAllLinesAsync(inductionPath, lines, ct);
                // Written after every episode so a crash or budget stop keeps what was learned.
                await File.WriteAllTextAsync(libraryPath, SkillJson.SerializeLibrary(config.Condition == "B" ? [] : persistent), ct);
            }
            await File.WriteAllLinesAsync(Path.Combine(config.OutputDir, "traces", $"seed_{seed}.jsonl"),
                trace.Select(t => JsonSerializer.Serialize(t, EpisodeRunner.TraceJson)), ct);
            log?.Invoke($"seed {seed,4}: {(result.Success ? "success" : "fail   ")} {result.EndReason,-22} " +
                        $"decisions {result.Decisions,3}  ticks {result.Ticks,4}  llm {result.LlmCalls,3} (cached {result.CacheHits})" +
                        (Induces(config.Condition) ? $"  induced {result.SkillsInduced} (calls {result.InduceCalls})  library {result.LibrarySize}" : ""));
        }

        var summary = new ExperimentSummary(
            results.Count,
            results.Count(r => r.Success),
            results.Count == 0 ? 0 : results.Count(r => r.Success) / (double)results.Count,
            results.Count == 0 ? 0 : results.Average(r => r.Decisions),
            results.Sum(r => r.LlmCalls),
            results.Sum(r => r.CacheHits),
            results.Sum(r => r.InputTokens),
            results.Sum(r => r.OutputTokens),
            aborted,
            results.Sum(r => r.SkillsInduced),
            results.Sum(r => r.InduceCalls),
            config.Condition is "C" or "E" ? persistent.Count : 0);

        var runInfo = new
        {
            condition = config.Condition,
            tier = tierName,
            seeds = config.Seeds,
            max_decisions = config.MaxDecisions,
            decider = decider.Name,
            provider = config.Provider,
            model = config.Model,
            temperature = config.Temperature,
            cache_mode = config.CacheMode.ToString().ToLowerInvariant(),
            cache_dir = config.CacheDir,
            budget = new { max_calls = config.Budget.MaxCalls, max_tokens = config.Budget.MaxTokens },
            library = config.Condition is "D" or "RD" or "B" or "C" or "E" ? config.LibrarySource : null,
            freeze = Induces(config.Condition) ? config.Freeze : (bool?)null,
            induce_attempts = Induces(config.Condition) ? config.InduceAttempts : (int?)null,
            induce_calls_per_episode = Induces(config.Condition) ? config.InduceCallsPerEpisode : (int?)null,
            skill_form = Induces(config.Condition) ? FormFor(config.Condition).ToString() : null,
            admission = Induces(config.Condition) ? config.Admission.ToString().ToLowerInvariant() : null,
            finished_utc = DateTime.UtcNow,
            summary
        };
        await File.WriteAllTextAsync(Path.Combine(config.OutputDir, "run.json"),
            JsonSerializer.Serialize(runInfo, new JsonSerializerOptions(EpisodeRunner.TraceJson) { WriteIndented = true }), ct);
        return summary;
    }

    // "1-20", "1,4,9", "5" → seed list.
    public static List<int> ParseSeeds(string text)
    {
        var seeds = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-', 1);
            if (dash > 0 && int.TryParse(part[..dash], out var from) && int.TryParse(part[(dash + 1)..], out var to) && from <= to)
                seeds.AddRange(Enumerable.Range(from, to - from + 1));
            else if (int.TryParse(part, out var single))
                seeds.Add(single);
            else
                throw new ArgumentException($"bad seed list part '{part}' (use e.g. 1-20 or 1,4,9)");
        }
        return seeds.Distinct().ToList();
    }
}
