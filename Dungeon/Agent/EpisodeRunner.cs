using System.Text.Json;
using System.Text.Json.Serialization;
using Dungeon.Core;

namespace Dungeon.Agent;

public sealed record EpisodeConfig(int Seed, ScenarioOptions Scenario, string Tier, int MaxDecisions, int MaxTicks = 500);

public sealed record StepTrace(
    int Decision,
    long TickBefore,
    long TickAfter,
    string Facts,
    string Action,
    IReadOnlyList<string> Args,
    string Reason,
    string Outcome,
    string Status,
    string? RawReply,
    string? ParseError,
    int InputTokens,
    int OutputTokens,
    bool FromCache);

// One row of metrics.csv (PLAN §9.4; skill columns join in week 6+).
public sealed record EpisodeResult(
    string Condition,
    string Tier,
    int Seed,
    bool Success,
    string EndReason,
    int Decisions,
    long Ticks,
    int InvalidActions,
    int ParseErrors,
    int LlmCalls,
    int CacheHits,
    long InputTokens,
    long OutputTokens,
    int ActionSetSize)
{
    public static readonly string CsvHeader =
        "condition,tier,seed,success,end_reason,decisions,ticks,invalid_actions,parse_errors,llm_calls,cache_hits,tokens_in,tokens_out,action_set_size";

    public string ToCsv() =>
        $"{Condition},{Tier},{Seed},{(Success ? 1 : 0)},{Csv(EndReason)},{Decisions},{Ticks},{InvalidActions},{ParseErrors},{LlmCalls},{CacheHits},{InputTokens},{OutputTokens},{ActionSetSize}";

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}

// Runs one episode: observe → decide → execute, until success, failure or a budget runs out.
public sealed class EpisodeRunner
{
    // Relaxed escaping keeps "->" and quotes readable in traces; traces are never embedded in HTML.
    public static readonly JsonSerializerOptions TraceJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<(EpisodeResult Result, IReadOnlyList<StepTrace> Trace)> RunAsync(
        EpisodeConfig config, string condition, IDecider decider, ActionSpace space, CancellationToken ct = default)
    {
        var world = new DungeonWorld(ScenarioGenerator.Generate(config.Seed, config.Scenario), config.Seed);
        var ctx = new AgentContext(world);
        var history = new List<string>();
        var trace = new List<StepTrace>();
        int invalid = 0, parseErrors = 0, calls = 0, hits = 0;
        long tokIn = 0, tokOut = 0;
        string endReason;

        for (var d = 1; ; d++)
        {
            if (ctx.World.Status == EpisodeStatus.Succeeded) { endReason = "exit"; break; }
            if (ctx.World.Status == EpisodeStatus.Failed) { endReason = ctx.World.FailureReason ?? "failed"; break; }
            if (d > config.MaxDecisions) { endReason = "decision_budget"; break; }
            if (ctx.Tick >= config.MaxTicks) { endReason = "tick_budget"; break; }

            var facts = FactSet.From(ctx);
            var record = await decider.DecideAsync(facts, space, history, config.MaxDecisions - d + 1, ct);
            if (record.RawReply is not null)
            {
                calls++;
                if (record.FromCache) hits++;
                tokIn += record.InputTokens;
                tokOut += record.OutputTokens;
            }

            var before = ctx.Tick;
            ActionOutcome outcome;
            if (record.ParseError is not null)
            {
                parseErrors++;
                outcome = new ActionOutcome("(unparseable reply)", "invalid", 0, record.ParseError);
                ctx.Memory.LastOutcome = outcome.ToString();
            }
            else
            {
                outcome = space.Execute(ctx, record.Decision.Action, record.Decision.Args);
            }
            if (outcome.IsInvalid)
                invalid++;
            history.Add(outcome.ToString());

            trace.Add(new StepTrace(d, before, ctx.Tick, facts.ToText(), record.Decision.Action, record.Decision.Args,
                record.Decision.Reason, outcome.ToString(), outcome.Status, record.RawReply, record.ParseError,
                record.InputTokens, record.OutputTokens, record.FromCache));
        }

        var result = new EpisodeResult(condition, config.Tier, config.Seed, endReason == "exit", endReason,
            trace.Count, ctx.Tick, invalid, parseErrors, calls, hits, tokIn, tokOut, space.Actions.Count);
        return (result, trace);
    }
}
