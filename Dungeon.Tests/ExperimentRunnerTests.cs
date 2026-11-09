using Dungeon.Agent;
using Dungeon.Llm;

namespace Dungeon.Tests;

public class ExperimentRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dungeon-exp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ExperimentConfig Config(string condition, string outName, CacheMode mode = CacheMode.Record) =>
        new(condition, "2", [1, 2, 3], 40, Path.Combine(_dir, outName), "stub", "stub-model", 0, mode,
            Path.Combine(_dir, "cache"), new LlmBudget());

    [Fact]
    public async Task Writes_run_json_metrics_and_one_trace_per_seed()
    {
        var config = Config("R", "r");
        var summary = await ExperimentRunner.RunAsync(config, new RuleBasedDecider());

        Assert.Equal(3, summary.Episodes);
        var csv = File.ReadAllLines(Path.Combine(config.OutputDir, "metrics.csv"));
        Assert.Equal(EpisodeResult.CsvHeader, csv[0]);
        Assert.Equal(4, csv.Length);
        Assert.All([1, 2, 3], s => Assert.True(File.Exists(Path.Combine(config.OutputDir, "traces", $"seed_{s}.jsonl"))));
        var run = File.ReadAllText(Path.Combine(config.OutputDir, "run.json"));
        Assert.Contains("\"decider\": \"rules\"", run);
        Assert.Contains("\"success_rate\"", run);
    }

    [Fact]
    public async Task Trace_lines_contain_facts_action_and_outcome()
    {
        var config = Config("R", "r");
        await ExperimentRunner.RunAsync(config, new RuleBasedDecider());
        var first = File.ReadLines(Path.Combine(config.OutputDir, "traces", "seed_1.jsonl")).First();
        Assert.Contains("\"facts\":", first);
        Assert.Contains("\"action\":", first);
        Assert.Contains("\"outcome\":", first);
    }

    [Fact]
    public async Task Replay_run_matches_recorded_run_row_for_row()
    {
        var rec = Config("A", "rec");
        await ExperimentRunner.RunAsync(rec, new LlmDecider(new CachingClient(new StubLlmClient(), rec.CacheDir, CacheMode.Record), "stub-model", 0));
        var rep = Config("A", "rep", CacheMode.Replay);
        await ExperimentRunner.RunAsync(rep, new LlmDecider(new CachingClient(null, rep.CacheDir, CacheMode.Replay), "stub-model", 0));

        static IEnumerable<string> Rows(string dir) =>
            File.ReadLines(Path.Combine(dir, "metrics.csv")).Skip(1).Select(l => string.Join(',', l.Split(',').Where((_, i) => i != 10)));
        Assert.Equal(Rows(rec.OutputDir), Rows(rep.OutputDir));
    }

    [Fact]
    public async Task Llm_failure_aborts_cleanly_and_keeps_finished_episodes()
    {
        var config = Config("A", "abort", CacheMode.Replay);
        var summary = await ExperimentRunner.RunAsync(config,
            new LlmDecider(new CachingClient(null, config.CacheDir, CacheMode.Replay), "stub-model", 0));
        Assert.Equal(0, summary.Episodes);
        Assert.Contains("replay cache miss", summary.AbortedReason);
        Assert.True(File.Exists(Path.Combine(config.OutputDir, "run.json")));
    }

    [Fact]
    public async Task Unknown_condition_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ExperimentRunner.RunAsync(Config("C", "c"), new RuleBasedDecider()));
    }

    [Theory]
    [InlineData("1-3", "1,2,3")]
    [InlineData("5", "5")]
    [InlineData("1,4,9", "1,4,9")]
    [InlineData("1-3,2,7", "1,2,3,7")]
    public void Seed_lists_parse(string text, string expected)
    {
        Assert.Equal(expected, string.Join(',', ExperimentRunner.ParseSeeds(text)));
    }

    [Theory]
    [InlineData("a-b")]
    [InlineData("3-1")]
    public void Bad_seed_lists_are_rejected(string text)
    {
        Assert.Throws<ArgumentException>(() => ExperimentRunner.ParseSeeds(text));
    }
}
