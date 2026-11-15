using System.Text.RegularExpressions;

namespace Dungeon.Llm;

// Offline stand-in for the model. Exists so the full pipeline (prompts, cache, parser, induction,
// runner, outputs) can be exercised without an API key. Its results are never research results;
// run.json labels them provider=stub.
//   choose_action: use a library skill on a visible obstacle if one is offered, else go to the exit
//                  if known, else explore.
//   induce_skill:  return a fixed, valid skill for the requested obstacle kind (or a macro for E).
public sealed partial class StubLlmClient : ILlmClient
{
    public int Calls { get; private set; }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        Calls++;
        var reply = request.PromptType == "induce_skill" ? Induce(request) : Choose(request);
        return Task.FromResult(new LlmResponse(reply, (request.System.Length + request.User.Length) / 4, reply.Length / 4));
    }

    private static string Choose(LlmRequest r)
    {
        // Use the first skill listed as usable now, if any.
        var usable = Usable().Match(r.User);
        if (usable.Success)
            return $"{{\"action\":\"{usable.Groups[1].Value}\",\"args\":[{string.Join(",", usable.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(a => $"\"{a}\""))}],\"reason\":\"use skill\"}}";
        // Collect a known key before exploring further.
        var key = KeyOnFloor().Match(r.User);
        if (key.Success)
            return key.Groups[2].Value == "0"
                ? "{\"action\":\"pick_up\",\"args\":[],\"reason\":\"get key\"}"
                : $"{{\"action\":\"go_to\",\"args\":[\"{key.Groups[1].Value}\"],\"reason\":\"get key\"}}";
        return ExitKnown().IsMatch(r.User)
            ? "{\"action\":\"go_to\",\"args\":[\"exit\"],\"reason\":\"exit known\"}"
            : "{\"action\":\"explore\",\"args\":[],\"reason\":\"search for the exit\"}";
    }

    private static string Induce(LlmRequest r)
    {
        var target = Regex.Match(r.User, @"Obstacle: (\w+), a (\w+)");
        var id = target.Groups[1].Value;
        var isDoor = target.Groups[2].Value != "guard";
        var n = Regex.Matches(r.User, @"^- stub_", RegexOptions.Multiline).Count + 1;
        if (r.System.Contains("You write macros"))
            return isDoor
                ? $$"""{"name":"stub_open_{{id}}","params":[],"pre":["known({{id}})"],"effect":["state({{id}}) == open"],"body":[{"call":"go_to","args":["{{id}}",1]},{"call":"use","args":["none","{{id}}"]}]}"""
                : $$"""{"name":"stub_calm_{{id}}","params":[],"pre":["known({{id}})"],"effect":["state({{id}}) in [calm, bribed, distracted]"],"body":[{"call":"go_to","args":["{{id}}",2]},{"call":"talk","args":["{{id}}"]}]}""";
        return isDoor
            ? $$"""{"name":"stub_open_{{n}}","description":"push or unlock a door","params":[{"name":"d","type":"door"}],"pre":["known(d)","state(d) == closed","type(d) == jammed or has_key(d)"],"effect":["state(d) == open"],"body":[{"until":"adjacent(d)","max":3,"do":[{"call":"go_to","args":["d",1]}]},{"until":"state(d) == open","max":6,"do":[{"if":"has_key(d)","then":[{"call":"use","args":["key_for(d)","d"]}],"else":[{"call":"use","args":["none","d"]}]}]}]}"""
            : $$"""{"name":"stub_calm_{{n}}","description":"talk to a guard from two cells, step back after a refusal","params":[{"name":"g","type":"guard"}],"pre":["known(g)","state(g) != hostile"],"effect":["state(g) in [calm, bribed, distracted]"],"body":[{"until":"state(g) in [calm, bribed, distracted]","max":4,"do":[{"until":"dist(g) <= 2","max":3,"do":[{"call":"go_to","args":["g",2]}]},{"call":"talk","args":["g"]},{"if":"state(g) == alert","then":[{"call":"retreat","args":["g",3]}]}]}]}""";
    }

    [GeneratedRegex(@"exit at \(\d+,\d+\)")]
    private static partial Regex ExitKnown();

    [GeneratedRegex(@"skills usable now: (?!none\b)(\w+)(?:\(([^)]*)\))?")]
    private static partial Regex Usable();

    [GeneratedRegex(@"(key_\d+) key \(opens \w+\) on floor at \([0-9,]+\), distance (\d+)")]
    private static partial Regex KeyOnFloor();
}
