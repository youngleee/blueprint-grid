using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dungeon.Llm;

// OpenAI Responses API client. JSON output mode, bounded retries on 429/5xx/timeouts.
public sealed class OpenAiClient : ILlmClient
{
    public const string DefaultEndpoint = "https://api.openai.com/v1/responses";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly int _maxAttempts;
    private readonly Func<int, TimeSpan> _backoff;

    public OpenAiClient(HttpClient http, string apiKey, string endpoint = DefaultEndpoint, int maxAttempts = 6,
        Func<int, TimeSpan>? backoff = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("OpenAI API key is empty", nameof(apiKey));
        _http = http;
        _apiKey = apiKey;
        _endpoint = endpoint;
        _maxAttempts = maxAttempts;
        _backoff = backoff ?? (attempt => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt))));
    }

    public static string BuildBody(LlmRequest r) => new JsonObject
    {
        ["model"] = r.Model,
        ["temperature"] = r.Temperature,
        ["max_output_tokens"] = r.MaxOutputTokens,
        ["input"] = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = r.System },
            new JsonObject { ["role"] = "user", ["content"] = r.User }
        },
        ["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_object" } }
    }.ToJsonString();

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var body = BuildBody(request);
        for (var attempt = 1; ; attempt++)
        {
            var sw = Stopwatch.StartNew();
            using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(message, ct);
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt >= _maxAttempts)
                    throw new LlmException($"OpenAI request failed after {attempt} attempts: {e.Message}", e);
                await Task.Delay(_backoff(attempt), ct);
                continue;
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                    return Parse(text, sw.Elapsed.TotalMilliseconds);

                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!retryable || attempt >= _maxAttempts)
                    throw new LlmException($"OpenAI returned {(int)response.StatusCode}: {Truncate(text, 300)}");
                await Task.Delay(RetryDelay(response, attempt), ct);
            }
        }
    }

    // Server-requested wait (Retry-After) when given, else exponential backoff; capped at 60 s.
    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var hint = response.Headers.RetryAfter?.Delta
                   ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        var delay = hint is { } h && h > TimeSpan.Zero ? h : _backoff(attempt);
        return delay > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : delay;
    }

    // Concatenates all output_text parts; reasoning or tool items are ignored. A refusal or an empty
    // or truncated output is returned as reply text, not thrown: it is the model's answer for this
    // prompt and must cost one decision (or one induction attempt), not abort the run. Only a body
    // that is not a Responses API object at all is an error.
    public static LlmResponse Parse(string json, double latencyMs = 0)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.Array)
                throw new LlmException($"not a Responses API object: {Truncate(json, 300)}");

            var parts = output.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                .SelectMany(item => item.GetProperty("content").EnumerateArray())
                .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("type", out _))
                .ToList();
            var sb = new StringBuilder();
            foreach (var p in parts.Where(p => p.GetProperty("type").GetString() == "output_text"))
                sb.Append(p.TryGetProperty("text", out var t) ? t.GetString() : "");

            if (sb.Length == 0)
            {
                var refusal = parts.FirstOrDefault(p => p.GetProperty("type").GetString() == "refusal");
                sb.Append(refusal.ValueKind == JsonValueKind.Object
                    ? $"(model refused: {(refusal.TryGetProperty("refusal", out var r) ? r.GetString() : "")})"
                    : "(empty model output)");
            }
            if (root.TryGetProperty("status", out var status) && status.GetString() == "incomplete")
            {
                var reason = root.TryGetProperty("incomplete_details", out var det) && det.ValueKind == JsonValueKind.Object
                             && det.TryGetProperty("reason", out var why) ? why.GetString() : "unknown";
                sb.Append($"\n(output truncated: {reason})");
            }

            int inTok = 0, outTok = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                inTok = usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
                outTok = usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ov) ? ov : 0;
            }
            return new LlmResponse(sb.ToString(), inTok, outTok, LatencyMs: latencyMs);
        }
        catch (JsonException e)
        {
            throw new LlmException($"OpenAI response was not JSON: {Truncate(json, 300)}", e);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
