using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Entities;
using Dungeon.Llm;
using Dungeon.Skills;

namespace Dungeon.Tests;

public class InducePromptTests
{
    [Fact]
    public void Examples_in_the_prompt_are_themselves_valid()
    {
        var space = StandardActions.Create();
        Assert.True(SkillValidator.Validate(SkillJson.Parse(InducePrompts.ExampleFetch), space).Ok);
        Assert.True(SkillValidator.Validate(SkillJson.Parse(InducePrompts.ExampleFindExit), space).Ok);
        var macro = SkillValidator.Validate(SkillJson.Parse(InducePrompts.ExampleMacro), space, null, new ValidationOptions(SkillForm.Macro));
        Assert.True(macro.Ok, macro.ToString());
    }

    [Fact]
    public void Examples_do_not_give_away_guard_or_door_solutions()
    {
        foreach (var e in new[] { InducePrompts.ExampleFetch, InducePrompts.ExampleFindExit, InducePrompts.ExampleMacro })
        {
            Assert.DoesNotContain("guard", e);
            Assert.DoesNotContain("door", e);
        }
    }

    [Fact]
    public void System_prompt_lists_actions_rules_and_test_bar()
    {
        var space = StandardActions.Create();
        var s = InducePrompts.System(space, SkillForm.ClosedLoop, new ScenarioTestOptions());
        Assert.All(space.Actions, a => Assert.Contains(a.Signature, s));
        Assert.Contains("never specific ids", s);
        Assert.Contains("at least 16 rooms", s);
        Assert.Contains("until", s);
    }

    [Fact]
    public void Macro_prompt_forbids_structure_and_allows_ids()
    {
        var s = InducePrompts.System(StandardActions.Create(), SkillForm.Macro, new ScenarioTestOptions());
        Assert.Contains("No parameters and no if / until / repeat / fail", s);
        Assert.DoesNotContain("{\"until\"", s);
    }

    [Fact]
    public void User_prompt_contains_target_facts_library_and_previous_feedback()
    {
        var target = new EntitySighting("guard_1", EntityKind.Guard, "guard", new Cell(3, 1), "alert", 4);
        var facts = FactSet.From(new AgentContext(TestMaps.World(TestMaps.Corridor)));
        var request = new InductionRequest("test trigger", target, facts, ["explore -> interrupted"]);
        var lib = HandwrittenSkills.Load();
        var u = InducePrompts.User(request, lib, SkillForm.ClosedLoop, [("{bad}", "- body: problem X")]);
        Assert.Contains("Obstacle: guard_1, a guard, currently alert", u);
        Assert.Contains(facts.ToText(), u);
        Assert.Contains("calm_guard(g)", u);
        Assert.Contains("Your attempt 1:", u);
        Assert.Contains("- body: problem X", u);
        Assert.Contains("corrected version", u);
    }

    [Fact]
    public void Induction_prompts_never_contain_hidden_entity_parameters()
    {
        for (var seed = 1; seed <= 30; seed++)
        {
            var scenario = ScenarioGenerator.Generate(seed, ScenarioOptions.Tier3);
            var ctx = new AgentContext(new DungeonWorld(scenario, seed));
            var target = new EntitySighting("guard_1", EntityKind.Guard, "guard", null, "idle", 0);
            var text = InducePrompts.System(StandardActions.Create(), SkillForm.ClosedLoop, new ScenarioTestOptions()) +
                       InducePrompts.User(new InductionRequest("t", target, FactSet.From(ctx), []), [], SkillForm.ClosedLoop, []);
            Assert.DoesNotContain("patience", text);
            foreach (var g in scenario.Guards)
                Assert.DoesNotContain(g.Traits.CalmChance.ToString("F2"), text);
        }
    }
}

public class SkillInducerTests
{
    private static readonly string GoodCalm = SkillJson.Serialize(SkillFixtures.CalmGuard("calm_guard_v3") with { Name = "calm_any" }, indented: false);
    private static readonly string WeakCalm = SkillJson.Serialize(SkillFixtures.CalmGuard("calm_guard_v2") with { Name = "calm_weak" }, indented: false);

    private static InductionRequest Request() => new("test",
        new EntitySighting("guard_1", EntityKind.Guard, "guard", new Cell(3, 1), "alert", 0),
        FactSet.From(new AgentContext(TestMaps.World(TestMaps.Corridor))), []);

    [Fact]
    public async Task Accepts_a_good_skill_on_the_first_attempt()
    {
        var client = new ScriptedLlmClient([GoodCalm]);
        var r = await new SkillInducer(client, "m", 0, SkillForm.ClosedLoop).InduceAsync(Request(), StandardActions.Create(), [], 3);
        Assert.Equal("calm_any", r.Accepted?.Name);
        Assert.Single(r.Attempts);
        Assert.Equal(InducePrompts.PromptType, client.Requests.Single().PromptType);
        Assert.Equal(InducePrompts.MaxOutputTokens, client.Requests.Single().MaxOutputTokens);
    }

    [Fact]
    public async Task Feeds_rejection_back_and_accepts_the_corrected_version()
    {
        var client = new ScriptedLlmClient(["not json", WeakCalm, GoodCalm]);
        var r = await new SkillInducer(client, "m", 0, SkillForm.ClosedLoop).InduceAsync(Request(), StandardActions.Create(), [], 3);

        Assert.Equal("calm_any", r.Accepted?.Name);
        Assert.Equal(3, r.Attempts.Count);
        Assert.Contains("could not be read as a skill", r.Attempts[0].Feedback);
        Assert.Contains("episode lost in", r.Attempts[1].Feedback);
        // Each retry prompt carries all earlier attempts and their feedback.
        Assert.Contains("could not be read as a skill", client.Requests[1].User);
        Assert.Contains("Your attempt 2:", client.Requests[2].User);
        Assert.Contains("episode lost in", client.Requests[2].User);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var client = new ScriptedLlmClient([WeakCalm, WeakCalm]);
        var r = await new SkillInducer(client, "m", 0, SkillForm.ClosedLoop).InduceAsync(Request(), StandardActions.Create(), [], 2);
        Assert.Null(r.Accepted);
        Assert.Equal(2, r.Attempts.Count);
        Assert.All(r.Attempts, a => Assert.False(a.Accepted));
    }

    [Fact]
    public async Task Macro_form_rejects_closed_loop_skills()
    {
        var client = new ScriptedLlmClient([GoodCalm]);
        var r = await new SkillInducer(client, "m", 0, SkillForm.Macro).InduceAsync(Request(), StandardActions.Create(), [], 1);
        Assert.Null(r.Accepted);
        Assert.Contains("a macro takes no parameters", r.Attempts[0].Feedback);
    }

    [Fact]
    public async Task Valid_admission_accepts_without_passing_tests_but_records_them()
    {
        var client = new ScriptedLlmClient([WeakCalm]);
        var r = await new SkillInducer(client, "m", 0, SkillForm.ClosedLoop, admission: AdmissionMode.Valid)
            .InduceAsync(Request(), StandardActions.Create(), [], 1);
        Assert.Equal("calm_weak", r.Accepted?.Name);
        Assert.Equal(20, r.Attempts[0].TestsRun);
    }
}

public class InductionPolicyTests
{
    private static readonly string GoodCalm = SkillJson.Serialize(SkillFixtures.CalmGuard("calm_guard_v3") with { Name = "calm_any" }, indented: false);

    // Guard in sight at distance 3 on the corridor.
    private static (AgentContext Ctx, ActionSpace Space) Setup() =>
        (new AgentContext(TestMaps.World(TestMaps.Corridor,
            guards: [new Guard("guard_1", new Cell(4, 1), TestMaps.Traits(patience: 9))], sightRadius: 3)),
         StandardActions.Create());

    [Fact]
    public async Task Induces_for_an_uncovered_obstacle_and_registers_the_skill()
    {
        var (ctx, space) = Setup();
        var library = new List<Skill>();
        var policy = new InductionPolicy(new SkillInducer(new ScriptedLlmClient([GoodCalm]), "m", 0, SkillForm.ClosedLoop));
        policy.StartEpisode();

        var r = await policy.MaybeInduceAsync(FactSet.From(ctx), [], space, library);

        Assert.NotNull(r?.Accepted);
        Assert.Equal(["calm_any"], library.Select(s => s.Name));
        Assert.Equal(ActionKind.Skill, space.Get("calm_any")!.Kind);
    }

    [Fact]
    public async Task Does_not_induce_when_a_usable_skill_covers_the_obstacle()
    {
        var (ctx, space) = Setup();
        var library = HandwrittenSkills.Load().ToList();
        space.WithSkills(library);
        var client = new ScriptedLlmClient([GoodCalm]);
        var policy = new InductionPolicy(new SkillInducer(client, "m", 0, SkillForm.ClosedLoop));
        policy.StartEpisode();

        Assert.Null(await policy.MaybeInduceAsync(FactSet.From(ctx), [], space, library));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Does_not_induce_without_an_obstacle()
    {
        var ctx = new AgentContext(TestMaps.World(TestMaps.Corridor));
        var client = new ScriptedLlmClient([GoodCalm]);
        var policy = new InductionPolicy(new SkillInducer(client, "m", 0, SkillForm.ClosedLoop));
        policy.StartEpisode();
        Assert.Null(await policy.MaybeInduceAsync(FactSet.From(ctx), [], StandardActions.Create(), []));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Tries_each_obstacle_once_and_respects_the_call_cap()
    {
        var (ctx, space) = Setup();
        var client = new ScriptedLlmClient(Enumerable.Repeat("not json", 10));
        var policy = new InductionPolicy(new SkillInducer(client, "m", 0, SkillForm.ClosedLoop), new InductionOptions(MaxCallsPerEpisode: 2, AttemptsPerRequest: 3));
        policy.StartEpisode();
        var facts = FactSet.From(ctx);

        var first = await policy.MaybeInduceAsync(facts, [], space, []);
        var second = await policy.MaybeInduceAsync(facts, [], space, []);
        var stalled = await policy.MaybeInduceAsync(facts, ["a -> failed", "b -> failed", "c -> invalid"], space, []);

        Assert.Equal(2, first!.Attempts.Count);
        Assert.Null(second);
        Assert.Null(stalled); // cap of 2 calls already spent
        Assert.Equal(2, client.Requests.Count);

        policy.StartEpisode();
        Assert.NotNull(await policy.MaybeInduceAsync(facts, [], space, []));
    }

    [Fact]
    public async Task Stall_triggers_a_second_request_for_the_same_obstacle()
    {
        var (ctx, space) = Setup();
        var client = new ScriptedLlmClient(Enumerable.Repeat("not json", 10));
        var policy = new InductionPolicy(new SkillInducer(client, "m", 0, SkillForm.ClosedLoop), new InductionOptions(MaxCallsPerEpisode: 6, AttemptsPerRequest: 3));
        policy.StartEpisode();
        var facts = FactSet.From(ctx);

        await policy.InduceOnceForTest(facts, space);
        var stalled = await policy.MaybeInduceAsync(facts, ["a -> failed", "b -> failed", "c -> invalid"], space, []);
        Assert.NotNull(stalled);
        Assert.StartsWith("stalled", stalled!.Request.Trigger);
    }
}

file static class PolicyTestExtensions
{
    public static async Task InduceOnceForTest(this InductionPolicy p, FactSet facts, ActionSpace space)
    {
        var r = await p.MaybeInduceAsync(facts, [], space, []);
        Assert.NotNull(r);
    }
}

public class UsableSkillsTests
{
    [Fact]
    public void Lists_skill_calls_whose_precondition_holds_and_effect_does_not()
    {
        // Door behind the guard's cell would block sight; put it beyond the guard, both in view.
        var guard = new Guard("guard_1", new Cell(3, 1), TestMaps.Traits(patience: 9));
        var door = new Door("door_1", new Cell(5, 1), DoorKind.Locked, 0, 0);
        var ctx = new AgentContext(TestMaps.World(TestMaps.Corridor, guards: [guard], doors: [door], sightRadius: 5));
        var space = StandardActions.Create().WithSkills(HandwrittenSkills.Load());

        var usable = UsableSkills.List(FactSet.From(ctx), space)!;

        Assert.Contains("calm_guard(guard_1)", usable);
        Assert.DoesNotContain("open_door(door_1)", usable); // locked, no key or lockpick
    }

    [Fact]
    public void Is_null_without_skills_and_shown_in_the_prompt_with_skills()
    {
        var ctx = new AgentContext(TestMaps.World(TestMaps.Corridor));
        Assert.Null(UsableSkills.List(FactSet.From(ctx), StandardActions.Create()));
        var space = StandardActions.Create().WithSkills(HandwrittenSkills.Load());
        var user = Prompts.ChooseActionUser(FactSet.From(ctx), [], 10, UsableSkills.List(FactSet.From(ctx), space));
        Assert.Contains("skills usable now: none", user);
    }

    [Fact]
    public void Skill_whose_effect_already_holds_is_invalid_not_a_silent_success()
    {
        var guard = new Guard("guard_1", new Cell(4, 1), TestMaps.Traits(patience: 9)) { State = GuardState.Calm };
        var ctx = new AgentContext(TestMaps.World(TestMaps.Corridor, guards: [guard], sightRadius: 5));
        var r = StandardActions.Create().WithSkills(HandwrittenSkills.Load()).Execute(ctx, "calm_guard", ["guard_1"]);
        Assert.True(r.IsInvalid);
        Assert.Contains("effect already holds", r.Message);
        Assert.Equal(0, ctx.Tick);
    }
}

public class InductionConditionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dungeon-induce-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ExperimentConfig Config(string condition, string name, IReadOnlyList<int> seeds, IReadOnlyList<Skill>? library = null,
        bool freeze = false) =>
        new(condition, "3", seeds, 40, Path.Combine(_dir, name), "stub", "stub-model", 0, CacheMode.Record,
            Path.Combine(_dir, "cache"), new LlmBudget(), library, library is null ? "none" : "file", freeze);

    private static (IDecider, SkillInducer) Stub(string condition)
    {
        var client = new StubLlmClient();
        return (new LlmDecider(client, "stub-model", 0), new SkillInducer(client, "stub-model", 0, ExperimentRunner.FormFor(condition)));
    }

    [Fact]
    public async Task C_keeps_learned_skills_across_episodes_and_writes_library_and_induction_log()
    {
        var config = Config("C", "c", [1, 2, 3, 4]);
        var (decider, inducer) = Stub("C");
        var summary = await ExperimentRunner.RunAsync(config, decider, inducer: inducer);

        Assert.True(summary.SkillsInduced >= 1);
        Assert.Equal(summary.SkillsInduced, summary.FinalLibrarySize);
        var lib = SkillJson.ParseLibrary(File.ReadAllText(Path.Combine(config.OutputDir, "library.json")));
        Assert.Equal(summary.FinalLibrarySize, lib.Count);
        Assert.True(File.ReadAllLines(Path.Combine(config.OutputDir, "induction.jsonl")).Length >= 1);
        // Later episodes start with the library: library_size never decreases.
        var sizes = File.ReadAllLines(Path.Combine(config.OutputDir, "metrics.csv")).Skip(1).Select(l => int.Parse(l.Split(',')[14])).ToList();
        Assert.Equal(sizes.Order(), sizes);
    }

    [Fact]
    public async Task B_starts_every_episode_without_skills()
    {
        var config = Config("B", "b", [1, 2, 3]);
        var (decider, inducer) = Stub("B");
        var summary = await ExperimentRunner.RunAsync(config, decider, inducer: inducer);
        Assert.Equal(0, summary.FinalLibrarySize);
        Assert.True(summary.SkillsInduced >= 3, "B should re-induce in every episode");
        Assert.Equal("[]", File.ReadAllText(Path.Combine(config.OutputDir, "library.json")).Trim());
    }

    [Fact]
    public async Task Frozen_library_is_used_without_induction_on_held_out_seeds()
    {
        var train = Config("C", "train", [1, 2, 3, 4]);
        var (d1, i1) = Stub("C");
        await ExperimentRunner.RunAsync(train, d1, inducer: i1);
        var library = SkillJson.ParseLibrary(File.ReadAllText(Path.Combine(train.OutputDir, "library.json")));

        var test = Config("C", "test", [101, 102, 103], library, freeze: true);
        var (d2, _) = Stub("C");
        var summary = await ExperimentRunner.RunAsync(test, d2);

        Assert.Equal(0, summary.InduceCalls);
        Assert.Equal(library.Count, summary.FinalLibrarySize);
        Assert.True(summary.Successes > 0);
    }

    [Fact]
    public async Task Induction_needs_an_inducer_unless_frozen_and_freeze_needs_an_inducing_condition()
    {
        var (decider, _) = Stub("C");
        await Assert.ThrowsAsync<ArgumentException>(() => ExperimentRunner.RunAsync(Config("C", "x", [1]), decider));
        await Assert.ThrowsAsync<ArgumentException>(() => ExperimentRunner.RunAsync(Config("A", "y", [1], freeze: true), decider));
    }

    [Fact]
    public async Task Induction_with_a_replayed_cache_is_reproducible()
    {
        var rec = Config("C", "rec", [1, 2, 3]);
        var c1 = new CachingClient(new StubLlmClient(), rec.CacheDir, CacheMode.Record);
        await ExperimentRunner.RunAsync(rec, new LlmDecider(c1, "stub-model", 0),
            inducer: new SkillInducer(c1, "stub-model", 0, SkillForm.ClosedLoop));

        var rep = Config("C", "rep", [1, 2, 3]) with { CacheMode = CacheMode.Replay };
        var c2 = new CachingClient(null, rep.CacheDir, CacheMode.Replay);
        var s2 = await ExperimentRunner.RunAsync(rep, new LlmDecider(c2, "stub-model", 0),
            inducer: new SkillInducer(c2, "stub-model", 0, SkillForm.ClosedLoop));

        Assert.Null(s2.AbortedReason);
        Assert.Equal(File.ReadAllText(Path.Combine(rec.OutputDir, "library.json")), File.ReadAllText(Path.Combine(rep.OutputDir, "library.json")));
        static IEnumerable<string> Rows(string dir) =>
            File.ReadLines(Path.Combine(dir, "metrics.csv")).Skip(1).Select(l => string.Join(',', l.Split(',').Where((_, i) => i != 10)));
        Assert.Equal(Rows(rec.OutputDir), Rows(rep.OutputDir));
    }
}
