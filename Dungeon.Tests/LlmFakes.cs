using System.Net;
using Dungeon.Llm;

namespace Dungeon.Tests;

// Replays fixed replies in order, recording every request. No network.
internal sealed class ScriptedLlmClient(IEnumerable<string> replies) : ILlmClient
{
    private readonly Queue<string> _replies = new(replies);
    public List<LlmRequest> Requests { get; } = new();

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        var text = _replies.Count > 0 ? _replies.Dequeue() : "{\"action\":\"wait\",\"args\":[],\"reason\":\"script exhausted\"}";
        return Task.FromResult(new LlmResponse(text, 100, 20));
    }
}

// HttpMessageHandler stub: returns queued responses and records requests.
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    public List<(HttpRequestMessage Request, string Body)> Seen { get; } = new();

    public StubHandler Enqueue(HttpStatusCode code, string body)
    {
        _responses.Enqueue(() => new HttpResponseMessage(code) { Content = new StringContent(body) });
        return this;
    }

    public StubHandler EnqueueThrow()
    {
        _responses.Enqueue(() => throw new HttpRequestException("connection reset"));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        return _responses.Dequeue()();
    }
}
