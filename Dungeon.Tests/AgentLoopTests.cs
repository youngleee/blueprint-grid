using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Entities;
using Dungeon.Llm;

namespace Dungeon.Tests;

public class DecisionParserTests
{
    [Theory]
    [InlineData("{\"action\":\"give\",\"args\":[\"food_1\",\"guard_1\"],\"reason\":\"r\"}", "give", "food_1|guard_1")]
    [InlineData("{\"action\":\"Explore\"}", "explore", "")]
    [InlineData("{\"Action\":\"move\",\"ARGS\":[\"N\"]}", "move", "N")]
    [InlineData("{\"action\":\"throw\",\"args\":[\"food_1\",4,7]}", "throw", "food_1|4|7")]
    [InlineData("{\"action\":\"give\",\"args\":\"food_1, guard_1\"}", "give", "food_1|guard_1")]
    [InlineData("{\"action\":\"wait\",\"args\":null}", "wait", "")]
    [InlineData("```json\n{\"action\":\"wait\"}\n```", "wait", "")]
    public void Accepts_reasonable_shapes(string reply, string action, string args)
    {
        Assert.True(DecisionParser.TryParse(reply, out var d, out _));
        Assert.Equal(action, d.Action);
        Assert.Equal(args, string.Join('|', d.Args));
    }

    [Theory]
    [InlineData("go north", "not valid JSON")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("{\"args\":[]}", "missing \"action\"")]
    [InlineData("{\"action\":\"\"}", "missing \"action\"")]
    [InlineData("{\"action\":\"move\",\"args\":5}", "must be a list")]
    public void Rejects_with_a_reason(string reply, string message)
    {
        Assert.False(DecisionParser.TryParse(reply, out _, out var error));
        Assert.Contains(message, error);
    }
}

public class PromptTests
{
    [Fact]
    public void System_prompt_lists_every_action_and_the_reply_format()
    {
        var space = StandardActions.Create();
        var system = Prompts.ChooseActionSystem(space);
        Assert.All(space.Actions, a => Assert.Contains(a.Signature, system));
        Assert.Contains("\"action\"", system);
        Assert.Contains("reach the exit", system);
    }

    [Fact]
    public void System_prompt_does_not_depend_on_the_episode()
    {
        Assert.Equal(Prompts.ChooseActionSystem(StandardActions.Create()), Prompts.ChooseActionSystem(StandardActions.Create()));
    }

    [Fact]
    public void User_prompt_contains_facts_budget_and_bounded_history()
    {
        var a = new AgentContext(new DungeonWorld(ScenarioGenerator.Generate(3, ScenarioOptions.Tier3), 3));
        var facts = FactSet.From(a);
        var history = Enumerable.Range(1, 10).Select(i => $"step {i}").ToList();
        var user = Prompts.ChooseActionUser(facts, history, decisionsLeft: 17);

        Assert.Contains("Decisions left: 17", user);
        Assert.Contains(facts.ToText(), user);
        Assert.DoesNotContain("step 4\n", user);
        Assert.Contains("step 5", user);
        Assert.Contains("step 10", user);
    }

    [Fact]
    public void Prompts_never_contain_hidden_entity_parameters()
    {
        for (var seed = 1; seed <= 50; seed++)
        {
            var scenario = ScenarioGenerator.Generate(seed, ScenarioOptions.Tier3);
            var a = new AgentContext(new DungeonWorld(scenario, seed));
            var text = Prompts.ChooseActionSystem(StandardActions.Create()) + Prompts.ChooseActionUser(FactSet.From(a), [], 30);
            Assert.DoesNotContain("patience", text);
            foreach (var g in scenario.Guards)
                Assert.DoesNotContain(g.Traits.CalmChance.ToString("F2"), text);
            foreach (var d in scenario.Doors.Where(d => d.PushChance > 0))
                Assert.DoesNotContain(d.PushChance.ToString("F2"), text);
        }
    }
}

public class DeciderTests
{
    [Fact]
    public async Task LlmDecider_sends_choose_action_request_and_parses_reply()
    {
        var client = new ScriptedLlmClient(["{\"action\":\"explore\",\"args\":[],\"reason\":\"look\"}"]);
        var decider = new LlmDecider(client, "gpt-test", 0);
        var a = new AgentContext(TestMaps.World(TestMaps.Corridor));
        var r = await decider.DecideAsync(FactSet.From(a), StandardActions.Create(), [], 10);

        Assert.Equal("explore", r.Decision.Action);
        Assert.Equal("look", r.Decision.Reason);
        Assert.Null(r.ParseError);
        var req = client.Requests.Single();
        Assert.Equal("gpt-test", req.Model);
        Assert.Equal(Prompts.ChooseActionType, req.PromptType);
    }

    [Fact]
    public async Task LlmDecider_turns_garbage_into_a_parse_error()
    {
        var decider = new LlmDecider(new ScriptedLlmClient(["I think I'll go north"]), "m", 0);
        var a = new AgentContext(TestMaps.World(TestMaps.Corridor));
        var r = await decider.DecideAsync(FactSet.From(a), StandardActions.Create(), [], 10);
        Assert.NotNull(r.ParseError);
        Assert.Equal("I think I'll go north", r.RawReply);
    }
}

public class EpisodeRunnerTests
{
    private static EpisodeConfig Config(int seed, ScenarioOptions tier, string name, int max = 60) =>
        new(seed, tier, name, max);

    [Fact]
    public async Task Rule_baseline_clears_door_tier_and_reports_metrics()
    {
        var (r, trace) = await EpisodeRunner.RunAsync(Config(1, ScenarioOptions.Tier2, "tier2"), "R",
            new RuleBasedDecider(), StandardActions.Create());
        Assert.True(r.Success, $"{r.EndReason} after {r.Decisions} decisions");
        Assert.Equal("exit", r.EndReason);
        Assert.Equal(trace.Count, r.Decisions);
        Assert.Equal(0, r.LlmCalls);
        Assert.Equal(11, r.ActionSetSize);
    }

    [Fact]
    public async Task Decision_budget_ends_the_episode()
    {
        var waits = Enumerable.Repeat("{\"action\":\"wait\"}", 10);
        var (r, trace) = await EpisodeRunner.RunAsync(Config(1, ScenarioOptions.Tier2, "tier2", max: 5), "A",
            new LlmDecider(new ScriptedLlmClient(waits), "m", 0), StandardActions.Create());
        Assert.False(r.Success);
        Assert.Equal("decision_budget", r.EndReason);
        Assert.Equal(5, r.Decisions);
        Assert.Equal(5, r.LlmCalls);
    }

    [Fact]
    public async Task Invalid_and_unparseable_replies_cost_a_decision_and_are_reported()
    {
        var client = new ScriptedLlmClient(["not json", "{\"action\":\"fly\"}", "{\"action\":\"wait\"}"]);
        var (r, trace) = await EpisodeRunner.RunAsync(Config(1, ScenarioOptions.Tier2, "tier2", max: 3), "A",
            new LlmDecider(client, "m", 0), StandardActions.Create());
        Assert.Equal(1, r.ParseErrors);
        Assert.Equal(2, r.InvalidActions);
        Assert.Equal(1, r.Ticks);
        // The model sees its own mistakes in the next prompt.
        Assert.Contains("unparseable", client.Requests[1].User);
        Assert.Contains("unknown action 'fly'", client.Requests[2].User);
    }

    [Fact]
    public async Task Hostile_guard_ends_episode_with_reason()
    {
        // Waiting next to an alert guard until its patience runs out.
        var guard = new Guard("guard_1", new Cell(3, 1), TestMaps.Traits(patience: 3));
        var ctx = new AgentContext(TestMaps.World(TestMaps.Corridor, guards: [guard]));
        var space = StandardActions.Create();
        var decider = new LlmDecider(new ScriptedLlmClient(Enumerable.Repeat("{\"action\":\"wait\"}", 10)), "m", 0);
        var history = new List<string>();
        while (!ctx.IsOver)
        {
            var d = await decider.DecideAsync(FactSet.From(ctx), space, history, 10);
            history.Add(space.Execute(ctx, d.Decision.Action, d.Decision.Args).ToString());
        }
        Assert.Equal(EpisodeStatus.Failed, ctx.World.Status);
        Assert.Contains("guard_1", ctx.World.FailureReason);
        // Tick 1: guard notices (alert, patience 3). Ticks 2-4 drain patience to 0 → hostile.
        Assert.Equal(4, history.Count);
    }

    [Fact]
    public async Task Full_llm_path_with_fake_model_is_deterministic()
    {
        async Task<string> Run()
        {
            var (r, trace) = await EpisodeRunner.RunAsync(Config(4, ScenarioOptions.Tier3, "tier3", max: 25), "A",
                new LlmDecider(new StubLlmClient(), "fake", 0), StandardActions.Create());
            return r.ToCsv() + string.Join("\n", trace.Select(t => t.Outcome));
        }
        Assert.Equal(await Run(), await Run());
    }

    [Fact]
    public async Task Replay_reproduces_a_recorded_run_exactly_without_the_model()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dungeon-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = Config(2, ScenarioOptions.Tier3, "tier3", max: 25);
            var live = new StubLlmClient();
            var (recorded, recordedTrace) = await EpisodeRunner.RunAsync(config, "A",
                new LlmDecider(new CachingClient(live, dir, CacheMode.Record), "fake", 0), StandardActions.Create());

            var (replayed, replayedTrace) = await EpisodeRunner.RunAsync(config, "A",
                new LlmDecider(new CachingClient(null, dir, CacheMode.Replay), "fake", 0), StandardActions.Create());

            Assert.Equal(recordedTrace.Select(t => t.Outcome), replayedTrace.Select(t => t.Outcome));
            Assert.Equal(recorded with { CacheHits = 0 }, replayed with { CacheHits = 0 });
            Assert.Equal(replayed.LlmCalls, replayed.CacheHits);
            Assert.True(live.Calls > 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Csv_row_matches_header()
    {
        var r = new EpisodeResult("A", "tier3", 1, true, "caught by guard_1, oops", 3, 9, 0, 0, 3, 0, 300, 30, 12, 2, 1, 1, 0, 0);
        Assert.Equal(EpisodeResult.CsvHeader.Split(',').Length, r.ToCsv().Split(',').Length - 1); // quoted comma
        Assert.Contains("\"caught by guard_1, oops\"", r.ToCsv());
    }
}
