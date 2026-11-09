using System.Text.RegularExpressions;

namespace Dungeon.Llm;

// Offline stand-in for the model: explore until the exit is in the facts, then go there.
// Exists so the full pipeline (prompts, cache, parser, runner, outputs) can be exercised without
// an API key. Its results are never research results; run.json labels them provider=stub.
public sealed partial class StubLlmClient : ILlmClient
{
    public int Calls { get; private set; }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        Calls++;
        var reply = ExitKnown().IsMatch(request.User)
            ? "{\"action\":\"go_to\",\"args\":[\"exit\"],\"reason\":\"exit known\"}"
            : "{\"action\":\"explore\",\"args\":[],\"reason\":\"search for the exit\"}";
        return Task.FromResult(new LlmResponse(reply, (request.System.Length + request.User.Length) / 4, reply.Length / 4));
    }

    [GeneratedRegex(@"exit at \(\d+,\d+\)")]
    private static partial Regex ExitKnown();
}
