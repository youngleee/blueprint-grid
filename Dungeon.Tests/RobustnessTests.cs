using System.Net;
using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Llm;
using Dungeon.Skills;

namespace Dungeon.Tests;

// Regression tests for the robustness audit (PLAN §14, "Robustness audit").
public class RobustnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dungeon-robust-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Env_file_inline_comments_are_stripped_as_in_env_example()
    {
        var v = LlmSettings.ParseEnvFile([
            "DUNGEON_LLM_PROVIDER=openai            # openai | stub",
            "OPENAI_MODEL=gpt-4.1-mini   # pin a dated snapshot",
            "OPENAI_API_KEY=",
            "QUOTED=\"a # not a comment\"  # comment",
            "HASHVALUE=abc#def"
        ]);
        Assert.Equal("openai", v["DUNGEON_LLM_PROVIDER"]);
        Assert.Equal("gpt-4.1-mini", v["OPENAI_MODEL"]);
        Assert.Equal("", v["OPENAI_API_KEY"]);
        Assert.Equal("a # not a comment", v["QUOTED"]);
        Assert.Equal("abc#def", v["HASHVALUE"]);
    }

    [Fact]
    public void Shipped_env_example_parses_to_clean_values()
    {
        var path = Path.Combine(RepoRoot(), ".env.example");
        var s = LlmSettings.Load(path);
        Assert.Equal("openai", s.Provider);
        Assert.DoesNotContain(" ", s.Model);
        Assert.DoesNotContain("#", s.CacheDir);
    }

    [Theory]
    [InlineData("""{"output":[{"type":"message","content":[{"type":"refusal","refusal":"no"}]}],"usage":{"input_tokens":5,"output_tokens":1}}""", "(model refused: no)")]
    [InlineData("""{"output":[]}""", "(empty model output)")]
    [InlineData("""{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[{"type":"message","content":[{"type":"output_text","text":"{\"a\""}]}]}""", "(output truncated: max_output_tokens)")]
    public void Refusals_and_truncation_are_replies_not_run_aborting_errors(string body, string expected)
    {
        Assert.Contains(expected, OpenAiClient.Parse(body).Text);
    }

    [Fact]
    public void Non_responses_body_is_still_an_error()
    {
        Assert.Throws<LlmException>(() => OpenAiClient.Parse("{\"error\":{\"message\":\"x\"}}"));
        Assert.Throws<LlmException>(() => OpenAiClient.Parse("[]"));
    }

    [Fact]
    public async Task Retry_after_header_is_honoured_and_capped()
    {
        var h = new StubHandler();
        h.Enqueue(HttpStatusCode.TooManyRequests, "slow down");
        h.Enqueue(HttpStatusCode.OK, """{"output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}]}""");
        var client = new OpenAiClient(new HttpClient(new RetryAfterHandler(h, TimeSpan.FromMilliseconds(200))), "sk", backoff: _ => TimeSpan.FromSeconds(30));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await client.CompleteAsync(new LlmRequest("m", 0, "t", "s", "u"));
        Assert.Equal("ok", r.Text);
        Assert.InRange(sw.ElapsedMilliseconds, 150, 5000);
    }

    [Fact]
    public async Task Corrupt_cache_entry_is_rerecorded_in_record_mode_and_named_in_replay_mode()
    {
        var req = new LlmRequest("m", 0, "choose_action", "s", "u");
        var record = new CachingClient(new ScriptedLlmClient(["first", "second"]), _dir, CacheMode.Record);
        await record.CompleteAsync(req);
        File.WriteAllText(record.PathFor(CachingClient.Key(req)), "{truncated");

        var again = await record.CompleteAsync(req);
        Assert.Equal("second", again.Text);
        Assert.False(again.FromCache);

        File.WriteAllText(record.PathFor(CachingClient.Key(req)), "{truncated");
        var e = await Assert.ThrowsAsync<LlmException>(() => new CachingClient(null, _dir, CacheMode.Replay).CompleteAsync(req));
        Assert.Contains("corrupt cache entry", e.Message);
    }

    [Fact]
    public async Task Concurrent_writers_of_the_same_prompt_do_not_collide()
    {
        var req = new LlmRequest("m", 0, "choose_action", "s", "u");
        var tasks = Enumerable.Range(0, 16).Select(i =>
            Task.Run(() => new CachingClient(new ScriptedLlmClient([$"r{i}"]), _dir, CacheMode.Live).CompleteAsync(req)));
        var results = await Task.WhenAll(tasks);
        Assert.Equal(16, results.Length);
        var stored = await new CachingClient(null, _dir, CacheMode.Replay).CompleteAsync(req);
        Assert.StartsWith("r", stored.Text);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Output_directory_must_be_empty()
    {
        var out1 = Path.Combine(_dir, "run");
        var config = new ExperimentConfig("R", "3", [1], 40, out1, "none", "-", 0, CacheMode.Record, Path.Combine(_dir, "c"), new LlmBudget());
        await ExperimentRunner.RunAsync(config, new RuleBasedDecider());
        var e = await Assert.ThrowsAsync<ArgumentException>(() => ExperimentRunner.RunAsync(config, new RuleBasedDecider()));
        Assert.Contains("not empty", e.Message);
    }

    [Fact]
    public async Task Cancellation_stops_between_decisions_and_still_writes_run_json()
    {
        using var cts = new CancellationTokenSource();
        var config = new ExperimentConfig("A", "3", Enumerable.Range(1, 50).ToList(), 40, Path.Combine(_dir, "cancel"), "stub", "m", 0,
            CacheMode.Record, Path.Combine(_dir, "c"), new LlmBudget());
        var decider = new CancelAfter(new LlmDecider(new StubLlmClient(), "m", 0), cts, after: 55);
        var summary = await ExperimentRunner.RunAsync(config, decider, ct: cts.Token);

        Assert.Equal("seed 2: interrupted", summary.AbortedReason);
        Assert.Equal(1, summary.Episodes);
        Assert.Contains("\"aborted_reason\": \"seed 2: interrupted\"", File.ReadAllText(Path.Combine(config.OutputDir, "run.json")));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(config.OutputDir, "metrics.csv")).Length);
    }

    [Fact]
    public void Effects_must_claim_progress()
    {
        Skill S(string effect) => SkillJson.Parse($$"""{"name":"s","params":[{"name":"g","type":"guard"}],"pre":["known(g)"],"effect":["{{effect}}"],"body":[{"until":"state(g) == calm","max":2,"do":[{"call":"talk","args":["g"]}]}]}""");
        string V(string effect) => SkillValidator.Validate(S(effect), StandardActions.Create()).ToString();

        Assert.Contains("must state progress", V("state(g) == alert"));
        Assert.Contains("must state progress", V("dist(g) > 1"));
        Assert.Contains("must state progress", V("known(g)"));
        Assert.Contains("must state progress", V("state(g) in [calm, alert]"));
        Assert.Equal("valid", V("state(g) in [calm, bribed, distracted]"));
        Assert.Equal("valid", V("state(g) == calm and dist(g) <= 3"));
        Assert.Equal("valid", V("at_exit()"));
    }

    [Fact]
    public void Library_files_with_name_clashes_or_unknown_calls_are_rejected_on_load()
    {
        const string clash = """[{"name":"explore","params":[],"pre":[],"effect":["at_exit()"],"body":[{"until":"at_exit()","max":2,"do":[{"call":"wait"}]}]}]""";
        const string unknown = """[{"name":"a","params":[],"pre":[],"effect":["at_exit()"],"body":[{"until":"at_exit()","max":2,"do":[{"call":"b"}]}]}]""";
        const string ordered = """[{"name":"a","params":[],"pre":[],"effect":["at_exit()"],"body":[{"until":"at_exit()","max":2,"do":[{"call":"wait"}]}]},{"name":"b","params":[],"pre":[],"effect":["at_exit()"],"body":[{"until":"at_exit()","max":2,"do":[{"call":"a"}]}]}]""";
        Assert.Contains("already an action", Assert.Throws<SkillFormatException>(() => SkillJson.LoadLibrary(clash, StandardActions.Create())).Message);
        Assert.Contains("unknown action 'b'", Assert.Throws<SkillFormatException>(() => SkillJson.LoadLibrary(unknown, StandardActions.Create())).Message);
        Assert.Equal(2, SkillJson.LoadLibrary(ordered, StandardActions.Create()).Count);
    }

    // Seed 1936 (tier 3) used to throw "only 2/3 chokepoints"; 8 of 20 000 tier-3 seeds did.
    [Fact]
    public void Seeds_whose_first_layout_is_too_short_get_an_alternative_layout()
    {
        var s = ScenarioGenerator.Generate(1936, ScenarioOptions.Tier3);
        Assert.Equal(1, s.Guards.Count);
        Assert.Equal(2, s.Doors.Count);
        foreach (var seed in new[] { 4088, 5870, 6186, 11826 })
            ScenarioGenerator.Generate(seed, ScenarioOptions.Tier3);
    }

    // The retry must not change any seed that worked before: its first attempt uses the original streams.
    [Fact]
    public void Seeds_that_generated_before_are_unchanged()
    {
        var s = ScenarioGenerator.Generate(1, ScenarioOptions.Tier3);
        Assert.Equal(new Cell(34, 28), s.Guards.Single().Position);
        Assert.Equal(["door_1@(11,18)", "door_2@(15,29)"], s.Doors.Select(d => $"{d.Id}@{d.Position}"));
        Assert.Equal(0.47, Math.Round(s.Guards.Single().Traits.CalmChance, 2));
    }

    [Fact]
    public void Every_protocol_seed_generates()
    {
        foreach (var (tier, from, to) in new[] { ("1", 1, 20), ("2", 21, 40), ("3", 41, 60), ("3", 1001, 1050), ("3", 2001, 2030) })
            for (var seed = from; seed <= to; seed++)
                ScenarioGenerator.Generate(seed, ExperimentRunner.TierOptions(tier));
    }

    // Garbage replies of every shape for 300 episodes: the run must never throw, and every
    // episode must end through a budget or the episode itself.
    [Fact]
    public async Task Fuzzed_model_replies_never_crash_an_episode()
    {
        var rng = new Random(7);
        string[] actions = ["move", "wait", "pick_up", "talk", "give", "throw", "use", "go_to", "explore", "retreat", "calm_guard", "open_door", "fly", ""];
        string[] tokens = ["guard_1", "door_1", "key_1", "food_2", "exit", "none", "N", "Q", "-1", "99999999999", "1.5", "", "(x)"];
        string Junk() => rng.Next(5) switch
        {
            0 => "not json",
            1 => "{\"action\":" + rng.Next(100) + "}",
            2 => "{\"action\":\"" + actions[rng.Next(actions.Length)] + "\",\"args\":[" +
                 string.Join(",", Enumerable.Range(0, rng.Next(5)).Select(_ => rng.Next(3) == 0 ? rng.Next(-5, 50).ToString() : $"\"{tokens[rng.Next(tokens.Length)]}\"")) + "]}",
            3 => "{\"action\":\"go_to\",\"args\":[{\"x\":[1]}]}",
            _ => "(model refused: no)"
        };
        for (var seed = 1; seed <= 300; seed++)
        {
            var client = new ScriptedLlmClient(Enumerable.Range(0, 40).Select(_ => Junk()));
            var (r, trace) = await EpisodeRunner.RunAsync(new EpisodeConfig(seed, ScenarioOptions.Tier3, "tier3", 40), "D",
                new LlmDecider(client, "m", 0), StandardActions.Create().WithSkills(HandwrittenSkills.Load()));
            Assert.Equal(trace.Count, r.Decisions);
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "GridBlueprint.sln")))
            d = d.Parent;
        return d!.FullName;
    }

    private sealed class CancelAfter(IDecider inner, CancellationTokenSource cts, int after) : IDecider
    {
        private int _n;
        public string Name => inner.Name;

        public Task<DecisionRecord> DecideAsync(FactSet facts, ActionSpace space, IReadOnlyList<string> history, int decisionsLeft,
            CancellationToken ct = default)
        {
            if (++_n == after)
                cts.Cancel();
            return inner.DecideAsync(facts, space, history, decisionsLeft, ct);
        }
    }

    private sealed class RetryAfterHandler(HttpMessageHandler inner, TimeSpan wait) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var r = await base.SendAsync(request, ct);
            if (r.StatusCode == HttpStatusCode.TooManyRequests)
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
            return r;
        }
    }
}
