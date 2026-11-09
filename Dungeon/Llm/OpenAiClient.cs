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

    public OpenAiClient(HttpClient http, string apiKey, string endpoint = DefaultEndpoint, int maxAttempts = 4,
        Func<int, TimeSpan>? backoff = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("OpenAI API key is empty", nameof(apiKey));
        _http = http;
        _apiKey = apiKey;
        _endpoint = endpoint;
        _maxAttempts = maxAttempts;
        _backoff = backoff ?? (attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));
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
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
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
                await Task.Delay(_backoff(attempt), ct);
            }
        }
    }

    // Concatenates all output_text parts; reasoning or tool items are ignored.
    public static LlmResponse Parse(string json, double latencyMs = 0)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var sb = new StringBuilder();
            if (root.TryGetProperty("output", out var output))
                foreach (var item in output.EnumerateArray())
                    if (item.TryGetProperty("content", out var content))
                        foreach (var part in content.EnumerateArray())
                            if (part.TryGetProperty("type", out var t) && t.GetString() == "output_text")
                                sb.Append(part.GetProperty("text").GetString());

            int inTok = 0, outTok = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                inTok = usage.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
                outTok = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
            }
            if (sb.Length == 0)
                throw new LlmException($"OpenAI response had no output_text: {Truncate(json, 300)}");
            return new LlmResponse(sb.ToString(), inTok, outTok, LatencyMs: latencyMs);
        }
        catch (JsonException e)
        {
            throw new LlmException($"OpenAI response was not JSON: {Truncate(json, 300)}", e);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
