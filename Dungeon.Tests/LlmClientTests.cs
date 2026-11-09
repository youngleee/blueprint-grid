using System.Net;
using System.Text.Json;
using Dungeon.Llm;

namespace Dungeon.Tests;

public class LlmClientTests
{
    private static readonly LlmRequest Request = new("gpt-test", 0, "choose_action", "sys", "user");

    private const string OkBody = """
        {"output":[{"type":"reasoning"},{"type":"message","content":[{"type":"output_text","text":"{\"action\":\"wait\"}"}]}],
         "usage":{"input_tokens":120,"output_tokens":8}}
        """;

    private static OpenAiClient Client(StubHandler h, int attempts = 3) =>
        new(new HttpClient(h), "sk-test", maxAttempts: attempts, backoff: _ => TimeSpan.Zero);

    [Fact]
    public async Task Sends_model_messages_json_mode_and_bearer_key()
    {
        var h = new StubHandler().Enqueue(HttpStatusCode.OK, OkBody);
        await Client(h).CompleteAsync(Request);

        var (req, body) = h.Seen.Single();
        Assert.Equal("Bearer sk-test", req.Headers.Authorization!.ToString());
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("gpt-test", root.GetProperty("model").GetString());
        Assert.Equal(0, root.GetProperty("temperature").GetDouble());
        Assert.Equal("json_object", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("system", root.GetProperty("input")[0].GetProperty("role").GetString());
        Assert.Equal("user", root.GetProperty("input")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Parses_output_text_and_usage_ignoring_other_items()
    {
        var r = await Client(new StubHandler().Enqueue(HttpStatusCode.OK, OkBody)).CompleteAsync(Request);
        Assert.Equal("{\"action\":\"wait\"}", r.Text);
        Assert.Equal(120, r.InputTokens);
        Assert.Equal(8, r.OutputTokens);
        Assert.False(r.FromCache);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Retries_transient_errors(HttpStatusCode code)
    {
        var h = new StubHandler().Enqueue(code, "busy").Enqueue(HttpStatusCode.OK, OkBody);
        var r = await Client(h).CompleteAsync(Request);
        Assert.Equal(2, h.Seen.Count);
        Assert.Equal(8, r.OutputTokens);
    }

    [Fact]
    public async Task Retries_network_failures()
    {
        var h = new StubHandler().EnqueueThrow().Enqueue(HttpStatusCode.OK, OkBody);
        await Client(h).CompleteAsync(Request);
        Assert.Equal(2, h.Seen.Count);
    }

    [Fact]
    public async Task Does_not_retry_client_errors()
    {
        var h = new StubHandler().Enqueue(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}");
        var e = await Assert.ThrowsAsync<LlmException>(() => Client(h).CompleteAsync(Request));
        Assert.Single(h.Seen);
        Assert.Contains("401", e.Message);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var h = new StubHandler().Enqueue(HttpStatusCode.TooManyRequests, "")
            .Enqueue(HttpStatusCode.TooManyRequests, "").Enqueue(HttpStatusCode.TooManyRequests, "");
        await Assert.ThrowsAsync<LlmException>(() => Client(h, attempts: 3).CompleteAsync(Request));
        Assert.Equal(3, h.Seen.Count);
    }

    [Fact]
    public void Response_without_text_is_an_error()
    {
        Assert.Throws<LlmException>(() => OpenAiClient.Parse("{\"output\":[]}"));
        Assert.Throws<LlmException>(() => OpenAiClient.Parse("<html>"));
    }

    [Fact]
    public void Empty_key_is_rejected_up_front()
    {
        Assert.Throws<ArgumentException>(() => new OpenAiClient(new HttpClient(), " "));
    }
}

public class CachingClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dungeon-cache-" + Guid.NewGuid().ToString("N"));
    private static readonly LlmRequest Request = new("m", 0, "choose_action", "sys", "user");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Record_calls_once_then_serves_from_cache()
    {
        var inner = new ScriptedLlmClient(["first", "second"]);
        var cache = new CachingClient(inner, _dir, CacheMode.Record);

        var a = await cache.CompleteAsync(Request);
        var b = await cache.CompleteAsync(Request);

        Assert.Single(inner.Requests);
        Assert.Equal("first", a.Text);
        Assert.False(a.FromCache);
        Assert.Equal("first", b.Text);
        Assert.True(b.FromCache);
        Assert.Equal(a.InputTokens, b.InputTokens);
    }

    [Fact]
    public async Task Replay_serves_recorded_entries_without_inner_client()
    {
        await new CachingClient(new ScriptedLlmClient(["recorded"]), _dir, CacheMode.Record).CompleteAsync(Request);
        var r = await new CachingClient(null, _dir, CacheMode.Replay).CompleteAsync(Request);
        Assert.Equal("recorded", r.Text);
        Assert.True(r.FromCache);
    }

    [Fact]
    public async Task Replay_miss_is_an_error()
    {
        var e = await Assert.ThrowsAsync<LlmException>(() => new CachingClient(null, _dir, CacheMode.Replay).CompleteAsync(Request));
        Assert.Contains("replay cache miss", e.Message);
    }

    [Fact]
    public async Task Live_always_calls_and_overwrites()
    {
        var inner = new ScriptedLlmClient(["one", "two"]);
        var cache = new CachingClient(inner, _dir, CacheMode.Live);
        await cache.CompleteAsync(Request);
        await cache.CompleteAsync(Request);
        Assert.Equal(2, inner.Requests.Count);
        Assert.Equal("two", (await new CachingClient(null, _dir, CacheMode.Replay).CompleteAsync(Request)).Text);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("temperature")]
    [InlineData("type")]
    [InlineData("system")]
    [InlineData("user")]
    [InlineData("tokens")]
    public void Key_changes_with_every_input_field(string field)
    {
        var changed = field switch
        {
            "model" => Request with { Model = "m2" },
            "temperature" => Request with { Temperature = 0.1 },
            "type" => Request with { PromptType = "induce_skill" },
            "system" => Request with { System = "sys2" },
            "user" => Request with { User = "user2" },
            _ => Request with { MaxOutputTokens = 301 }
        };
        Assert.NotEqual(CachingClient.Key(Request), CachingClient.Key(changed));
    }

    [Fact]
    public void Key_is_stable()
    {
        Assert.Equal(CachingClient.Key(Request), CachingClient.Key(Request with { }));
        Assert.Equal(64, CachingClient.Key(Request).Length);
    }

    [Fact]
    public void Non_replay_modes_need_a_client()
    {
        Assert.Throws<ArgumentException>(() => new CachingClient(null, _dir, CacheMode.Record));
    }
}

public class MeteredClientTests
{
    private static readonly LlmRequest Request = new("m", 0, "choose_action", "s", "u");

    [Fact]
    public async Task Counts_calls_and_tokens()
    {
        var m = new MeteredClient(new ScriptedLlmClient(["a", "b"]), new LlmBudget());
        await m.CompleteAsync(Request);
        await m.CompleteAsync(Request);
        Assert.Equal(2, m.Calls);
        Assert.Equal(200, m.InputTokens);
        Assert.Equal(40, m.OutputTokens);
        Assert.Equal(240, m.Tokens);
    }

    [Fact]
    public async Task Stops_at_call_budget()
    {
        var m = new MeteredClient(new ScriptedLlmClient(["a", "b", "c"]), new LlmBudget(MaxCalls: 2));
        await m.CompleteAsync(Request);
        await m.CompleteAsync(Request);
        await Assert.ThrowsAsync<BudgetExceededException>(() => m.CompleteAsync(Request));
    }

    [Fact]
    public async Task Stops_at_token_budget()
    {
        var m = new MeteredClient(new ScriptedLlmClient(["a", "b"]), new LlmBudget(MaxTokens: 100));
        await m.CompleteAsync(Request);
        await Assert.ThrowsAsync<BudgetExceededException>(() => m.CompleteAsync(Request));
    }

    [Fact]
    public async Task Cache_hits_do_not_count_against_the_budget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dungeon-meter-" + Guid.NewGuid().ToString("N"));
        try
        {
            var meter = new MeteredClient(new ScriptedLlmClient(["a"]), new LlmBudget(MaxCalls: 1));
            var client = new CachingClient(meter, dir, CacheMode.Record);
            await client.CompleteAsync(Request);
            var again = await client.CompleteAsync(Request);
            Assert.True(again.FromCache);
            Assert.Equal(1, meter.Calls);
            await Assert.ThrowsAsync<BudgetExceededException>(() => client.CompleteAsync(Request with { User = "new" }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class LlmSettingsTests
{
    [Fact]
    public void Env_file_parser_handles_comments_quotes_and_blanks()
    {
        var values = LlmSettings.ParseEnvFile([
            "# comment",
            "",
            "OPENAI_API_KEY=\"sk-abc\"",
            "OPENAI_MODEL = gpt-4.1-mini",
            "BROKEN_LINE",
            "QUOTED='x=y'"
        ]);
        Assert.Equal("sk-abc", values["OPENAI_API_KEY"]);
        Assert.Equal("gpt-4.1-mini", values["OPENAI_MODEL"]);
        Assert.Equal("x=y", values["QUOTED"]);
        Assert.False(values.ContainsKey("BROKEN_LINE"));
    }

    [Fact]
    public void Describe_never_prints_the_key()
    {
        var s = new LlmSettings("openai", "sk-secret-123", "m", 0, "c");
        Assert.DoesNotContain("sk-secret", s.Describe());
        Assert.Contains("key=set", s.Describe());
    }
}
