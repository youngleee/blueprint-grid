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
    LlmBudget Budget);

public sealed record ExperimentSummary(
    int Episodes,
    int Successes,
    double SuccessRate,
    double MeanDecisions,
    int LlmCalls,
    int CacheHits,
    long InputTokens,
    long OutputTokens,
    string? AbortedReason);

// Runs one condition over a list of seeds and writes run.json, metrics.csv and one trace per episode.
//   <out>/run.json            config, provider (never the key), summary
//   <out>/metrics.csv         one row per episode (EpisodeResult)
//   <out>/traces/seed_N.jsonl one line per decision (StepTrace)
public static class ExperimentRunner
{
    // A: LLM, primitives + helpers · R: rules, same actions · D: LLM + hand-written skills · RD: rules + same skills
    public static readonly string[] Conditions = ["A", "R", "D", "RD"];

    public static bool UsesLlm(string condition) => condition is "A" or "D";

    public static ActionSpace InitialSpace(string condition) => condition switch
    {
        "D" or "RD" => StandardActions.Create().WithSkills(HandwrittenSkills.Load()),
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
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (!Conditions.Contains(config.Condition))
            throw new ArgumentException($"condition '{config.Condition}' is not implemented yet (available: {string.Join(", ", Conditions)})");

        var tierName = config.Tier.StartsWith("tier") ? config.Tier : $"tier{config.Tier}";
        var options = TierOptions(config.Tier);
        Directory.CreateDirectory(Path.Combine(config.OutputDir, "traces"));
        var csvPath = Path.Combine(config.OutputDir, "metrics.csv");
        await File.WriteAllTextAsync(csvPath, EpisodeResult.CsvHeader + "\n", ct);

        var results = new List<EpisodeResult>();
        string? aborted = null;
        foreach (var seed in config.Seeds)
        {
            // Every episode starts from the same action set; nothing carries over in A, R, D, RD.
            var space = InitialSpace(config.Condition);
            EpisodeResult result;
            IReadOnlyList<StepTrace> trace;
            try
            {
                (result, trace) = await EpisodeRunner.RunAsync(
                    new EpisodeConfig(seed, options, tierName, config.MaxDecisions), config.Condition, decider, space, ct);
            }
            catch (LlmException e)
            {
                aborted = $"seed {seed}: {e.Message}";
                log?.Invoke($"ABORTED {aborted}");
                break;
            }

            results.Add(result);
            await File.AppendAllTextAsync(csvPath, result.ToCsv() + "\n", ct);
            await File.WriteAllLinesAsync(Path.Combine(config.OutputDir, "traces", $"seed_{seed}.jsonl"),
                trace.Select(t => JsonSerializer.Serialize(t, EpisodeRunner.TraceJson)), ct);
            log?.Invoke($"seed {seed,4}: {(result.Success ? "success" : "fail   ")} {result.EndReason,-22} " +
                        $"decisions {result.Decisions,3}  ticks {result.Ticks,4}  llm {result.LlmCalls,3} (cached {result.CacheHits})");
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
            aborted);

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
