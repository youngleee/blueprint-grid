using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Entities;
using Dungeon.Skills;

namespace Dungeon.Tests;

public class SkillInterpreterTests
{
    private static Skill S(string json) => SkillJson.Parse(json);

    private static (AgentContext Ctx, ActionSpace Space) Setup(DungeonWorld world, params Skill[] skills) =>
        (new AgentContext(world), StandardActions.Create().WithSkills(skills));

    private static ActionOutcome Exec(AgentContext ctx, ActionSpace space, string name, params string[] args) =>
        space.Execute(ctx, name, args);

    private const string WaitTwice = """
        {"name":"wait_twice","params":[],"pre":[],"effect":[],"body":[{"repeat":2,"do":[{"call":"wait"}]}]}
        """;

    [Fact]
    public void Skill_is_one_decision_that_runs_several_ticks()
    {
        var (ctx, space) = Setup(TestMaps.World(TestMaps.Corridor), S(WaitTwice));
        var r = Exec(ctx, space, "wait_twice");
        Assert.Equal("success", r.Status);
        Assert.Equal(2, r.Ticks);
        Assert.Equal(2, r.Steps.Count);
        Assert.Equal(r.ToString(), ctx.Memory.LastOutcome);
    }

    [Fact]
    public void Skill_appears_in_the_action_space_with_its_contract()
    {
        var space = StandardActions.Create().WithSkills(HandwrittenSkills.Load());
        var calm = space.Get("calm_guard")!;
        Assert.Equal(ActionKind.Skill, calm.Kind);
        Assert.Equal("calm_guard(g)", calm.Signature);
        Assert.Contains("Needs: known(g) and state(g) != hostile", calm.Description);
        Assert.Contains("Achieves: state(g) in [calm, bribed, distracted]", calm.Description);
        Assert.Contains("calm_guard(g)", Prompts.ChooseActionSystem(space));
    }

    [Fact]
    public void Unmet_precondition_is_invalid_and_costs_nothing()
    {
        var (ctx, space) = Setup(TestMaps.World(TestMaps.Corridor), S("""
            {"name":"needs_food","params":[],"pre":["has(food)"],"effect":[],"body":[{"call":"wait"}]}
            """));
        var r = Exec(ctx, space, "needs_food");
        Assert.True(r.IsInvalid);
        Assert.Contains("precondition not met: has(food)", r.Message);
        Assert.Equal(0, ctx.Tick);
    }

    [Fact]
    public void Missed_effect_is_failed_with_the_condition_named()
    {
        var (ctx, space) = Setup(TestMaps.World(TestMaps.Corridor), S("""
            {"name":"wish","params":[],"pre":[],"effect":["has(coin)"],"body":[{"call":"wait"}]}
            """));
        var r = Exec(ctx, space, "wish");
        Assert.Equal("failed", r.Status);
        Assert.Contains("effect not reached: has(coin)", r.Message);
        Assert.Equal(1, r.Ticks);
    }

    [Fact]
    public void If_takes_the_branch_matching_current_facts()
    {
        const string json = """
            {"name":"branch","params":[],"pre":[],"effect":[],
             "body":[{"if":"has(food)","then":[{"call":"wait"}],"else":[{"call":"pick_up"}]}]}
            """;
        var map = TestMaps.Corridor;
        var (ctx, space) = Setup(TestMaps.World(map, items: [TestMaps.AtStart(map, "food_1", ItemType.Food)]), S(json));

        Assert.StartsWith("pick_up", Exec(ctx, space, "branch").Steps.Single());
        Assert.StartsWith("wait", Exec(ctx, space, "branch").Steps.Single());
    }

    [Fact]
    public void Until_reevaluates_after_every_pass_and_stops_early()
    {
        // Jammed door with push chance 1: opens on the first push, so only one pass of max 5 runs.
        var door = new Door("door_1", new Cell(2, 1), DoorKind.Jammed, 0, 1.0);
        var (ctx, space) = Setup(TestMaps.World(TestMaps.Corridor, doors: [door]), S("""
            {"name":"push","params":[{"name":"d","type":"door"}],"pre":[],"effect":["state(d) == open"],
             "body":[{"until":"state(d) == open","max":5,"do":[{"call":"use","args":["none","d"]}]}]}
            """));
        var r = Exec(ctx, space, "push", "door_1");
        Assert.Equal("success", r.Status);
        Assert.Single(r.Steps);
    }

    [Fact]
    public void Until_stops_at_max_and_effect_decides()
    {
        var door = new Door("door_1", new Cell(2, 1), DoorKind.Jammed, 0, 0.0);
        var (ctx, space) = Setup(TestMaps.World(TestMaps.Corridor, doors: [door]), S("""
            {"name":"push","params":[{"name":"d","type":"door"}],"pre":[],"effect":["state(d) == open"],
             "body":[{"until":"state(d) == open","max":3,"do":[{"call":"use","args":["none","d"]}]}]}
            """));
        var r = Exec(ctx, space, "push", "door_1");
        Assert.Equal("failed", r.Status);
        Assert.Equal(3, r.Steps.Count);
        Assert.Contains("effect not reached", r.Message);
    }

    [Fact]
    public void Fail_statement_stops_with_its_reason()
    {
        var (ctx, space) = Setup(TestMaps.World(TestMaps.Corridor), S("""
            {"name":"nope","params":[],"pre":[],"effect":[],"body":[{"fail":"not today"},{"call":"wait"}]}
            """));
        var r = Exec(ctx, space, "nope");
        Assert.Equal("failed", r.Status);
        Assert.Equal("not today", r.Message);
        Assert.Equal(0, ctx.Tick);
    }

    [Fact]
    public void Invalid_step_stops_the_skill()
    {
        var (ctx, space) = Setup(TestMaps.World(TestMaps.Corridor), S("""
            {"name":"bad","params":[],"pre":[],"effect":[],"body":[{"call":"talk","args":["guard_9"]},{"call":"wait"}]}
            """));
        var r = Exec(ctx, space, "bad");
        Assert.Equal("failed", r.Status);
        Assert.Contains("step talk(guard_9) was invalid", r.Message);
        Assert.Equal(0, ctx.Tick);
    }

    [Fact]
    public void Argument_functions_resolve_against_memory()
    {
        var map = TestMaps.Corridor;
        var guard = new Guard("guard_1", new Cell(5, 1), TestMaps.Traits(toss: 1.0, patience: 9));
        var (ctx, space) = Setup(TestMaps.World(map, guards: [guard],
                items: [TestMaps.AtStart(map, "food_1", ItemType.Food)], sightRadius: 5), S("""
            {"name":"toss_at","params":[{"name":"g","type":"guard"}],"pre":[],"effect":["state(g) == distracted"],
             "body":[{"call":"pick_up"},{"call":"go_to","args":["g",3]},{"call":"throw","args":["held(food)","pos(g)"]}]}
            """));
        // pos(g) expands to the two arguments x y of the guard's last-known cell.
        var r = Exec(ctx, space, "toss_at", "guard_1");
        Assert.Equal("success", r.Status);
        Assert.Contains("throw(food_1,(5,1)) -> success", string.Join("\n", r.Steps));
    }

    [Fact]
    public void Unresolvable_argument_fails_the_skill_without_a_tick()
    {
        var (ctx, space) = Setup(TestMaps.World(TestMaps.Corridor), S("""
            {"name":"gift","params":[],"pre":[],"effect":[],"body":[{"call":"give","args":["held(coin)","guard_1"]}]}
            """));
        var r = Exec(ctx, space, "gift");
        Assert.Equal("failed", r.Status);
        Assert.Contains("held(coin): not holding one", r.Message);
        Assert.Equal(0, ctx.Tick);
    }

    [Fact]
    public void Skills_can_call_skills_and_nesting_is_bounded()
    {
        var inner = S(WaitTwice);
        var outer = S("""{"name":"outer","params":[],"pre":[],"effect":[],"body":[{"call":"wait_twice"},{"call":"wait"}]}""");
        var self = S("""{"name":"loop","params":[],"pre":[],"effect":[],"body":[{"call":"loop"}]}""");
        var (ctx, space) = Setup(TestMaps.World(TestMaps.Corridor), inner, outer, self);

        var r = Exec(ctx, space, "outer");
        Assert.Equal("success", r.Status);
        Assert.Equal(3, r.Ticks);
        Assert.Contains(r.Steps, s => s.StartsWith("  wait"));

        var loop = Exec(ctx, space, "loop");
        Assert.Equal("failed", loop.Status);
        Assert.Contains("nesting deeper than 3", string.Join("\n", loop.Steps));
        Assert.Equal(0, ctx.SkillDepth);
    }

    [Fact]
    public void Failure_of_a_nested_skill_fails_the_caller()
    {
        var inner = S("""{"name":"inner","params":[],"pre":[],"effect":["has(coin)"],"body":[{"call":"wait"}]}""");
        var outer = S("""{"name":"outer","params":[],"pre":[],"effect":[],"body":[{"call":"inner"},{"call":"wait"}]}""");
        var (ctx, space) = Setup(TestMaps.World(TestMaps.Corridor), inner, outer);
        var r = Exec(ctx, space, "outer");
        Assert.Equal("failed", r.Status);
        Assert.Contains("step inner failed: effect not reached: has(coin)", r.Message);
        Assert.Equal(1, ctx.Tick);
    }

    [Fact]
    public void Step_and_tick_limits_bound_runaway_skills()
    {
        var skill = S("""{"name":"long","params":[],"pre":[],"effect":[],"body":[{"repeat":50,"do":[{"call":"wait"}]}]}""");
        var ctx = new AgentContext(TestMaps.World(TestMaps.Corridor));
        var space = StandardActions.Create().WithSkills([skill], new SkillLimits(MaxSteps: 10));
        var r = space.Execute(ctx, "long", []);
        Assert.Equal("failed", r.Status);
        Assert.Contains("step limit of 10", r.Message);
        Assert.Equal(10, ctx.Tick);
    }

    [Fact]
    public void Episode_end_inside_a_skill_is_reported()
    {
        var guard = new Guard("guard_1", new Cell(3, 1), TestMaps.Traits(patience: 2));
        var (ctx, space) = Setup(TestMaps.World(TestMaps.Corridor, guards: [guard]), S(
            """{"name":"stall","params":[],"pre":[],"effect":[],"body":[{"repeat":10,"do":[{"call":"wait"}]}]}"""));
        var r = Exec(ctx, space, "stall");
        Assert.Equal("episode_over", r.Status);
        Assert.Contains("caught by guard_1", r.Message);
        Assert.True(ctx.IsOver);
    }
}

public class HandwrittenSkillTests
{
    private static readonly ActionSpace Space = StandardActions.Create().WithSkills(HandwrittenSkills.Load());

    [Fact]
    public void Calm_guard_switches_strategy_when_throwing_does_not_work()
    {
        // Throw never distracts, talk always works: the skill must fall through to talking.
        var map = TestMaps.Corridor;
        var guard = new Guard("guard_1", new Cell(4, 1), TestMaps.Traits(calm: 1.0, toss: 0.0, patience: 9));
        var ctx = new AgentContext(TestMaps.World(map, guards: [guard],
            items: [TestMaps.AtStart(map, "food_1", ItemType.Food)], sightRadius: 4));
        Space.Execute(ctx, "pick_up", []);

        var r = Space.Execute(ctx, "calm_guard", ["guard_1"]);

        Assert.Equal("success", r.Status);
        Assert.Equal(GuardState.Calm, ctx.World.Guards.Single().State);
        var steps = string.Join("\n", r.Steps);
        Assert.Contains("throw(food_1,(4,1)) -> failed: nobody reacted", steps);
        Assert.Contains("talk(guard_1) -> success", steps);
    }

    [Fact]
    public void Calm_guard_backs_off_after_a_refusal_and_tries_again()
    {
        // Talk works with p=0.5; over seeds, some runs must refuse first, retreat, and then succeed.
        var retried = 0;
        for (var seed = 1; seed <= 30; seed++)
        {
            var guard = new Guard("guard_1", new Cell(4, 1), TestMaps.Traits(calm: 0.5, patience: 9));
            var ctx = new AgentContext(TestMaps.World(TestMaps.Corridor, guards: [guard], sightRadius: 4, seed: seed));
            var r = Space.Execute(ctx, "calm_guard", ["guard_1"]);
            var steps = string.Join("\n", r.Steps);
            if (r.Status == "success" && steps.Contains("talk(guard_1) -> failed") && steps.Contains("retreat(guard_1,3) -> arrived"))
                retried++;
        }
        Assert.True(retried > 0, "no seed showed refuse → retreat → succeed");
    }

    [Fact]
    public void Calm_guard_throws_food_from_a_distance()
    {
        var map = TestMaps.Corridor;
        var guard = new Guard("guard_1", new Cell(4, 1), TestMaps.Traits(toss: 1.0, patience: 9));
        var ctx = new AgentContext(TestMaps.World(map, guards: [guard],
            items: [TestMaps.AtStart(map, "food_1", ItemType.Food)], sightRadius: 4));
        Space.Execute(ctx, "pick_up", []);
        var r = Space.Execute(ctx, "calm_guard", ["guard_1"]);
        Assert.Equal("success", r.Status);
        Assert.Equal(GuardState.Distracted, ctx.World.Guards.Single().State);
        Assert.Equal(new Cell(1, 1), ctx.Position);
    }

    [Fact]
    public void Calm_guard_bribes_with_coin_when_talking_fails()
    {
        var map = TestMaps.Corridor;
        var guard = new Guard("guard_1", new Cell(4, 1), TestMaps.Traits(coin: 1.0, patience: 9));
        var ctx = new AgentContext(TestMaps.World(map, guards: [guard],
            items: [TestMaps.AtStart(map, "coin_1", ItemType.Coin)], sightRadius: 4));
        Space.Execute(ctx, "pick_up", []);
        Assert.Equal("success", Space.Execute(ctx, "calm_guard", ["guard_1"]).Status);
        Assert.Equal(GuardState.Bribed, ctx.World.Guards.Single().State);
    }

    [Fact]
    public void Calm_guard_reports_failure_when_nothing_works()
    {
        var guard = new Guard("guard_1", new Cell(4, 1), TestMaps.Traits(patience: 9));
        var ctx = new AgentContext(TestMaps.World(TestMaps.Corridor, guards: [guard], sightRadius: 4));
        var r = Space.Execute(ctx, "calm_guard", ["guard_1"]);
        Assert.Equal("failed", r.Status);
        Assert.Contains("effect not reached", r.Message);
        Assert.Equal(EpisodeStatus.Running, ctx.World.Status);
    }

    [Theory]
    [InlineData(DoorKind.Locked, "key")]
    [InlineData(DoorKind.Locked, "lockpick")]
    [InlineData(DoorKind.Jammed, "none")]
    public void Open_door_uses_the_right_tool(DoorKind kind, string tool)
    {
        var map = TestMaps.Corridor;
        var door = new Door("door_1", new Cell(5, 1), kind, pickChance: 1.0, pushChance: 1.0);
        Item[] items = tool switch
        {
            "key" => [TestMaps.AtStart(map, "key_1", ItemType.Key, opens: "door_1")],
            "lockpick" => [TestMaps.AtStart(map, "lockpick_1", ItemType.Lockpick)],
            _ => []
        };
        var ctx = new AgentContext(TestMaps.World(map, doors: [door], items: items, sightRadius: 5));
        if (items.Length > 0)
            Space.Execute(ctx, "pick_up", []);

        var r = Space.Execute(ctx, "open_door", ["door_1"]);

        Assert.Equal("success", r.Status);
        Assert.True(ctx.World.Doors.Single().IsOpen);
        Assert.Contains(tool == "key" ? "use(key_1,door_1)" : tool == "lockpick" ? "use(lockpick_1,door_1)" : "use(none,door_1)",
            string.Join("\n", r.Steps));
    }

    [Fact]
    public void Open_door_fails_cleanly_on_locked_door_without_tools()
    {
        var door = new Door("door_1", new Cell(5, 1), DoorKind.Locked, 0, 0);
        var ctx = new AgentContext(TestMaps.World(TestMaps.Corridor, doors: [door], sightRadius: 5));
        var r = Space.Execute(ctx, "open_door", ["door_1"]);
        Assert.Equal("failed", r.Status);
        Assert.Equal("locked and no key or lockpick", r.Message);
    }

    [Fact]
    public void Open_door_precondition_rejects_open_door()
    {
        var door = new Door("door_1", new Cell(2, 1), DoorKind.Jammed, 0, 1.0, isOpen: true);
        var ctx = new AgentContext(TestMaps.World(TestMaps.Corridor, doors: [door], sightRadius: 5));
        Assert.Contains("precondition not met: state(d) == closed", Space.Execute(ctx, "open_door", ["door_1"]).Message);
    }

    // Closed-loop evidence at unit level: over many guard-trait draws the same skill produces
    // different step sequences, because each branch depends on the guard's actual response.
    [Fact]
    public void Calm_guard_takes_different_paths_for_different_guards()
    {
        var patterns = new HashSet<string>();
        for (var seed = 1; seed <= 40; seed++)
        {
            var map = TestMaps.Corridor;
            var guard = new Guard("guard_1", new Cell(4, 1), TestMaps.Traits(calm: 0.5, toss: 0.5, patience: 9));
            var ctx = new AgentContext(TestMaps.World(map, guards: [guard],
                items: [TestMaps.AtStart(map, "food_1", ItemType.Food)], sightRadius: 4, seed: seed));
            Space.Execute(ctx, "pick_up", []);
            var r = Space.Execute(ctx, "calm_guard", ["guard_1"]);
            patterns.Add(string.Join(">", r.Steps.Where(s => !s.StartsWith(' ')).Select(s => s[..s.IndexOf("->")])));
        }
        Assert.True(patterns.Count >= 3, $"only {patterns.Count} distinct paths");
    }
}

public class SkillConditionTests
{
    [Fact]
    public async Task Rules_with_skills_beat_rules_without_on_guard_tier()
    {
        async Task<int> Successes(string condition)
        {
            var space = ExperimentRunner.InitialSpace(condition);
            var wins = 0;
            for (var seed = 1; seed <= 40; seed++)
            {
                var (r, _) = await EpisodeRunner.RunAsync(new EpisodeConfig(seed, ScenarioOptions.Tier1, "tier1", 40),
                    condition, new RuleBasedDecider(), space);
                if (r.Success) wins++;
            }
            return wins;
        }

        var r = await Successes("R");
        var rd = await Successes("RD");
        Assert.True(rd > r, $"RD {rd}/40 should beat R {r}/40");
    }

    [Fact]
    public async Task Skill_metrics_and_trace_steps_are_recorded()
    {
        var (result, trace) = await EpisodeRunner.RunAsync(new EpisodeConfig(1, ScenarioOptions.Tier3, "tier3", 40),
            "RD", new RuleBasedDecider(), ExperimentRunner.InitialSpace("RD"));
        Assert.Equal(2, result.LibrarySize);
        Assert.True(result.SkillInvocations > 0);
        var skillStep = trace.First(t => t.Kind == "skill");
        Assert.NotNull(skillStep.SkillSteps);
        Assert.NotEmpty(skillStep.SkillSteps!);
    }
}
