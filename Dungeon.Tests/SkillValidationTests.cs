using Dungeon.Agent;
using Dungeon.Skills;

namespace Dungeon.Tests;

internal static class SkillFixtures
{
    public static List<Skill> Library(string name) =>
        SkillJson.ParseLibrary(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{name}.json")));

    public static Skill CalmGuard(string name) => Library(name).First(s => s.Name == "calm_guard");
}

public class SkillValidatorTests
{
    private static readonly ActionSpace Space = StandardActions.Create();

    private static ValidationResult V(string json, ValidationOptions? o = null, IEnumerable<Skill>? existing = null) =>
        SkillValidator.Validate(SkillJson.Parse(json), Space, existing, o);

    private static string Skill(string body, string ps = """[{"name":"g","type":"guard"}]""",
        string pre = """["known(g)"]""", string effect = """["state(g) == calm"]""", string name = "s") =>
        $$"""{"name":"{{name}}","params":{{ps}},"pre":{{pre}},"effect":{{effect}},"body":{{body}}}""";

    private const string Ok = """[{"until":"state(g) == calm","max":3,"do":[{"call":"talk","args":["g"]}]}]""";

    [Fact]
    public void Handwritten_library_is_valid()
    {
        var space = StandardActions.Create();
        foreach (var s in HandwrittenSkills.Load())
        {
            var r = SkillValidator.Validate(s, space);
            Assert.True(r.Ok, r.ToString());
            space.Register(SkillActions.ToAction(s));
        }
    }

    [Fact]
    public void Minimal_closed_loop_skill_is_valid()
    {
        Assert.True(V(Skill(Ok)).Ok);
    }

    [Theory]
    [InlineData("""[{"until":"state(g) == calm","max":3,"do":[{"call":"fly","args":["g"]}]}]""", "body[0].do[0].call: unknown action 'fly'")]
    [InlineData("""[{"until":"state(g) == calm","max":3,"do":[{"call":"talk"}]}]""", "talk(guard) takes 1 argument(s), got 0")]
    [InlineData("""[{"until":"state(g) == calm","max":3,"do":[{"call":"give","args":["g","g"]}]}]""", "argument 1 of give (item) must be an item, got a guard")]
    [InlineData("""[{"until":"state(g) == calm","max":3,"do":[{"call":"throw","args":["held(food)","g"]}]}]""", "throw(item, x, y) takes 3 argument(s), got 2")]
    [InlineData("""[{"until":"state(g) == calm","max":3,"do":[{"call":"throw","args":["held(food)","pos(g)"]}]}]""", null)]
    [InlineData("""[{"until":"state(g) == calm","max":3,"do":[{"call":"go_to","args":["g"]}]}]""", null)]
    [InlineData("""[{"until":"state(g) == calm","max":3,"do":[{"call":"go_to","args":["g",1,2]}]}]""", "go_to(target, [distance]) takes 1-2 argument(s), got 3")]
    [InlineData("""[{"until":"state(g) == calm","max":3,"do":[{"call":"talk","args":["guard_1"]}]}]""", "names the specific entity 'guard_1'; use a parameter so the skill works for any guard")]
    [InlineData("""[{"until":"state(g) == calm","max":3,"do":[{"call":"talk","args":["bob"]}]}]""", "unknown name 'bob'")]
    [InlineData("""[{"until":"state(g) == calm","max":3,"do":[{"call":"use","args":["key_for(g)","g"]}]}]""", "key_for(d) needs a door parameter")]
    [InlineData("""[{"until":"state(g) == calm","max":3,"do":[{"call":"give","args":["held(dragon)","g"]}]}]""", "held(t) needs an item type")]
    [InlineData("""[{"until":"state(g) == calm","max":9,"do":[{"call":"talk","args":["g"]}]}]""", "body[0].max: at most 6")]
    [InlineData("""[{"until":"state(g) == calm","max":3,"do":[{"call":"s","args":["g"]}]}]""", "calls itself")]
    [InlineData("""[{"until":"state(g) == calm","max":6,"do":[{"repeat":6,"do":[{"call":"talk","args":["g"]},{"call":"wait"}]}]}]""", "up to 72 calls in the worst case")]
    public void Body_checks(string body, string? message)
    {
        var r = V(Skill(body));
        if (message is null)
            Assert.True(r.Ok, r.ToString());
        else
            Assert.Contains(message, r.ToString());
    }

    [Theory]
    [InlineData("""["known(g)", "state(guard_1) == calm"]""", "pre[1]: names the specific entity 'guard_1'")]
    [InlineData("""["known(x)"]""", "pre[0]: unknown name 'x'")]
    [InlineData("""["known(g) and state(g) in [alert, idle]"]""", null)]
    public void Condition_checks(string pre, string? message)
    {
        var r = V(Skill(Ok, pre: pre));
        if (message is null)
            Assert.True(r.Ok, r.ToString());
        else
            Assert.Contains(message, r.ToString());
    }

    [Fact]
    public void Fixed_sequence_is_rejected_as_a_macro()
    {
        var r = V(Skill("""[{"call":"talk","args":["g"]},{"call":"talk","args":["g"]}]"""));
        Assert.Contains("a fixed sequence of calls is a macro, not a skill", r.ToString());
    }

    [Fact]
    public void Constant_condition_does_not_count_as_closed_loop()
    {
        Assert.Contains("is a macro", V(Skill("""[{"if":"true","then":[{"call":"talk","args":["g"]}]}]""")).ToString());
    }

    [Fact]
    public void Unused_parameter_is_reported()
    {
        var r = V(Skill(Ok, ps: """[{"name":"g","type":"guard"},{"name":"d","type":"door"}]"""));
        Assert.Contains("params: 'd' is never used", r.ToString());
    }

    [Fact]
    public void Missing_effect_is_reported()
    {
        Assert.Contains("effect: must declare at least one condition", V(Skill(Ok, effect: "[]")).ToString());
    }

    [Fact]
    public void Name_clash_with_an_action_is_reported()
    {
        Assert.Contains("'talk' is already an action", V(Skill(Ok, name: "talk")).ToString());
    }

    [Fact]
    public void Parameter_named_like_a_fact_word_is_rejected()
    {
        var r = V(Skill("""[{"until":"state(food) == calm","max":3,"do":[{"call":"talk","args":["food"]}]}]""",
            ps: """[{"name":"food","type":"guard"}]""", pre: "[]", effect: """["state(food) == calm"]"""));
        Assert.Contains("'food' is a fact word", r.ToString());
    }

    [Fact]
    public void All_errors_are_reported_at_once()
    {
        var r = V(Skill("""[{"call":"fly"},{"call":"talk","args":["guard_1"]}]""", effect: "[]"));
        Assert.True(r.Errors.Count >= 4, r.ToString());
    }

    [Fact]
    public void Renamed_copy_of_an_existing_skill_is_a_duplicate()
    {
        var original = SkillJson.Parse(Skill(Ok, name: "calm"));
        var r = V(Skill(Ok, name: "calm_again"), existing: [original]);
        Assert.Contains("same parameters, contract and body as existing skill 'calm'", r.ToString());
    }

    [Fact]
    public void Macro_form_accepts_fixed_sequences_with_literal_ids_and_rejects_structure()
    {
        var macro = new ValidationOptions(SkillForm.Macro);
        const string json = """{"name":"m","params":[],"pre":["known(guard_1)"],"effect":["state(guard_1) == calm"],"body":[{"call":"go_to","args":["guard_1",2]},{"call":"talk","args":["guard_1"]}]}""";
        var ok = V(json, macro);
        Assert.True(ok.Ok, ok.ToString());

        var bad = V(Skill(Ok), macro);
        Assert.Contains("a macro takes no parameters", bad.ToString());
        Assert.Contains("a macro may not loop", bad.ToString());
    }

    [Fact]
    public void Worst_case_calls_counts_loops_and_takes_the_longer_branch()
    {
        const string body = """[{"call":"wait"},{"until":"state(g) == calm","max":3,"do":[{"if":"has(food)","then":[{"call":"wait"},{"call":"wait"}],"else":[{"call":"wait"}]}]}]""";
        Assert.Equal(7, SkillValidator.WorstCaseCalls(SkillJson.Parse(Skill(body)).Body));
    }
}

public class ScenarioTesterTests
{
    [Fact]
    public void Accepts_both_good_calm_guard_drafts_and_rejects_the_weak_one()
    {
        // Full-episode RD success on 200 tier-3 seeds: v1 97.5 %, v3 95.5 %, v2 85.5 % (R: 87.5 %).
        Assert.True(ScenarioTester.Test(SkillFixtures.CalmGuard("calm_guard_v1"), StandardActions.Create()).Accepted);
        Assert.True(ScenarioTester.Test(SkillFixtures.CalmGuard("calm_guard_v3"), StandardActions.Create()).Accepted);
        var weak = ScenarioTester.Test(SkillFixtures.CalmGuard("calm_guard_v2"), StandardActions.Create());
        Assert.False(weak.Accepted);
        Assert.True(weak.Lost > 0, "v2 should be rejected for losing episodes");
    }

    // Reproduces the week-7 calibration (PLAN §6.4): 40 disjoint blocks of 20 rooms each.
    // Deterministic (fixed seeds), so the exact counts are pinned as a regression check.
    [Fact]
    public void Calibration_counts_are_reproduced()
    {
        int Accepted(string name) => Enumerable.Range(0, 40)
            .Count(b => ScenarioTester.Test(SkillFixtures.CalmGuard(name), StandardActions.Create(),
                new ScenarioTestOptions(SeedBase: 5_000_000 + b * 1000)).Accepted);
        Assert.Equal(39, Accepted("calm_guard_v1"));
        Assert.Equal(37, Accepted("calm_guard_v3"));
        Assert.Equal(4, Accepted("calm_guard_v2"));
    }

    [Fact]
    public void Handwritten_library_passes_its_own_tests()
    {
        var space = StandardActions.Create();
        foreach (var s in HandwrittenSkills.Load())
        {
            var r = ScenarioTester.Test(s, space);
            Assert.True(r.Accepted, r.ToString());
            space.Register(SkillActions.ToAction(s));
        }
    }

    [Fact]
    public void Rooms_satisfy_the_precondition_and_start_with_the_effect_unmet()
    {
        var r = ScenarioTester.Test(SkillFixtures.CalmGuard("calm_guard_v3"), StandardActions.Create());
        Assert.Equal(20, r.Cases.Count);
        Assert.All(r.Cases, c => Assert.DoesNotContain("precondition not met", c.Outcome));
    }

    [Fact]
    public void Skill_whose_precondition_never_holds_is_rejected_with_a_reason()
    {
        var never = SkillJson.Parse("""{"name":"never","params":[{"name":"g","type":"guard"}],"pre":["known(g)","has(tool)"],"effect":["state(g) == calm"],"body":[{"until":"state(g) == calm","max":2,"do":[{"call":"talk","args":["g"]}]}]}""");
        var r = ScenarioTester.Test(never, StandardActions.Create());
        Assert.False(r.Accepted);
        Assert.Contains("no test room satisfied the precondition", r.Error);
    }

    [Fact]
    public void Test_rooms_are_identical_for_every_skill()
    {
        var a = ScenarioTester.Test(SkillFixtures.CalmGuard("calm_guard_v1"), StandardActions.Create());
        var b = ScenarioTester.Test(SkillFixtures.CalmGuard("calm_guard_v3"), StandardActions.Create());
        Assert.Equal(a.Cases.Select(c => (c.Seed, c.Setup)), b.Cases.Select(c => (c.Seed, c.Setup)));
    }

    [Fact]
    public void Testing_is_deterministic()
    {
        var a = ScenarioTester.Test(SkillFixtures.CalmGuard("calm_guard_v3"), StandardActions.Create());
        var b = ScenarioTester.Test(SkillFixtures.CalmGuard("calm_guard_v3"), StandardActions.Create());
        Assert.Equal(a.Cases, b.Cases);
    }

    [Fact]
    public void Testing_does_not_modify_the_given_action_space()
    {
        var space = StandardActions.Create();
        ScenarioTester.Test(SkillFixtures.CalmGuard("calm_guard_v3"), space);
        Assert.Null(space.Get("calm_guard"));
    }
}

public class SkillAdmissionTests
{
    [Fact]
    public void Invalid_skill_is_not_tested_and_feedback_lists_errors()
    {
        var bad = SkillJson.Parse("""{"name":"x","params":[],"pre":[],"effect":[],"body":[{"call":"fly"}]}""");
        var r = SkillAdmission.Check(bad, StandardActions.Create());
        Assert.False(r.Accepted);
        Assert.Null(r.Tests);
        Assert.StartsWith("The skill was rejected by the validator:", r.Feedback());
        Assert.Contains("- body[0].call: unknown action 'fly'", r.Feedback());
    }

    [Fact]
    public void Feedback_for_a_rejected_skill_shows_each_test_room()
    {
        var r = SkillAdmission.Check(SkillFixtures.CalmGuard("calm_guard_v2"), StandardActions.Create());
        Assert.False(r.Accepted);
        var fb = r.Feedback();
        Assert.Contains("episode lost in", fb);
        Assert.Contains("fail (episode lost)", fb);
        Assert.Equal(20, fb.Split('\n').Count(l => l.StartsWith("- ")));
    }

    [Fact]
    public void Blind_food_skill_is_stopped_by_the_validator()
    {
        var r = SkillAdmission.Check(SkillFixtures.CalmGuard("calm_guard_blind"), StandardActions.Create());
        Assert.False(r.Accepted);
        Assert.Contains("is a macro", r.Feedback());
    }
}
