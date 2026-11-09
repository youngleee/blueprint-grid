using Dungeon.Agent;
using Dungeon.Skills;

namespace Dungeon.Tests;

public class SkillJsonTests
{
    private const string Minimal = """
        {"name":"wait_twice","params":[],"pre":[],"effect":[],"body":[{"repeat":2,"do":[{"call":"wait"}]}]}
        """;

    [Fact]
    public void Parses_every_statement_kind_and_argument_form()
    {
        var s = SkillJson.Parse("""
            {
              "name": "demo", "description": "all forms",
              "params": [{"name":"g","type":"guard"},{"name":"d","type":"door"}],
              "pre": ["known(g)"], "effect": ["state(g) == calm"],
              "body": [
                {"call":"go_to","args":["g", 1]},
                {"if":"has(food)","then":[{"call":"give","args":["held(food)","g"]}],"else":[{"call":"talk","args":["g"]}]},
                {"until":"state(g) == calm","max":3,"do":[{"call":"throw","args":["held(food)","pos(g)"]}]},
                {"repeat":2,"do":[{"call":"use","args":["key_for(d)","d"]}]},
                {"fail":"gave up"}
              ]
            }
            """);
        Assert.Equal("demo", s.Name);
        Assert.Equal([ParamType.Guard, ParamType.Door], s.Params.Select(p => p.Type));
        Assert.IsType<CallStmt>(s.Body[0]);
        Assert.Equal(["g", "1"], ((CallStmt)s.Body[0]).Args.Select(a => a.ToString()));
        Assert.IsType<IfStmt>(s.Body[1]);
        var until = Assert.IsType<UntilStmt>(s.Body[2]);
        Assert.Equal(3, until.Max);
        Assert.Equal(new FuncArg("pos", ["g"]), ((CallStmt)until.Body[0]).Args[1]);
        Assert.Equal(2, Assert.IsType<RepeatStmt>(s.Body[3]).Count);
        Assert.Equal("gave up", Assert.IsType<FailStmt>(s.Body[4]).Reason);
    }

    [Fact]
    public void Serialize_round_trips()
    {
        foreach (var skill in HandwrittenSkills.Load().Append(SkillJson.Parse(Minimal)))
        {
            var once = SkillJson.Serialize(skill);
            Assert.Equal(once, SkillJson.Serialize(SkillJson.Parse(once)));
        }
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[]", "skill: expected an object")]
    [InlineData("""{"params":[],"pre":[],"effect":[],"body":[{"call":"wait"}]}""", "skill: missing \"name\"")]
    [InlineData("""{"name":"Bad Name","params":[],"pre":[],"effect":[],"body":[{"call":"wait"}]}""", "skill.name: 'Bad Name' must be lowercase")]
    [InlineData("""{"name":"x","params":[{"name":"g","type":"dragon"}],"pre":[],"effect":[],"body":[{"call":"wait"}]}""", "skill.params[0].type: unknown type 'dragon'")]
    [InlineData("""{"name":"x","params":[{"name":"g","type":"guard"},{"name":"g","type":"door"}],"pre":[],"effect":[],"body":[{"call":"wait"}]}""", "duplicate parameter 'g'")]
    [InlineData("""{"name":"x","params":[{"name":"exit","type":"guard"}],"pre":[],"effect":[],"body":[{"call":"wait"}]}""", "reserved word")]
    [InlineData("""{"name":"x","params":[],"pre":["state(g)"],"effect":[],"body":[{"call":"wait"}]}""", "skill.pre[0]: state(...) needs a comparison")]
    [InlineData("""{"name":"x","params":[],"pre":[],"effect":[],"body":[]}""", "skill.body: must contain at least one statement")]
    [InlineData("""{"name":"x","params":[],"pre":[],"effect":[],"body":[{"call":"wait","if":"true"}]}""", "skill.body[0]: a statement needs exactly one of")]
    [InlineData("""{"name":"x","params":[],"pre":[],"effect":[],"body":[{"until":"true","do":[{"call":"wait"}]}]}""", "skill.body[0]: missing \"max\"")]
    [InlineData("""{"name":"x","params":[],"pre":[],"effect":[],"body":[{"until":"true","max":0,"do":[{"call":"wait"}]}]}""", "skill.body[0].max: expected a whole number")]
    [InlineData("""{"name":"x","params":[],"pre":[],"effect":[],"body":[{"if":"true","then":[{"call":"wait"}],"elsee":[]}]}""", "unknown key \"elsee\"")]
    [InlineData("""{"name":"x","params":[],"pre":[],"effect":[],"body":[{"call":"give","args":["near(g)"]}]}""", "skill.body[0].args[0]: unknown function 'near'")]
    [InlineData("""{"name":"x","params":[],"pre":[],"effect":[],"body":[{"call":"give","args":["a b"]}]}""", "is not a name, number or function call")]
    [InlineData("""{"name":"x","params":[],"pre":[],"effect":[],"body":[{"if":"true","then":[{"repeat":2,"do":[{"call":"wait","args":"g"}]}]}]}""", "skill.body[0].then[0].do[0].args: expected a list")]
    public void Malformed_skills_report_the_exact_location(string json, string message)
    {
        Assert.False(SkillJson.TryParse(json, out _, out var error));
        Assert.Contains(message, error);
    }

    [Fact]
    public void Library_rejects_duplicate_names()
    {
        var e = Assert.Throws<SkillFormatException>(() => SkillJson.ParseLibrary($"[{Minimal},{Minimal}]"));
        Assert.Contains("appears twice", e.Message);
    }

    [Fact]
    public void Handwritten_library_loads()
    {
        Assert.Equal(["calm_guard", "open_door"], HandwrittenSkills.Load().Select(s => s.Name));
    }
}
