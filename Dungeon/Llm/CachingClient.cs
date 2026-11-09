using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dungeon.Llm;

public enum CacheMode
{
    // Always call the inner client, overwrite the cache entry.
    Live,

    // Use the cache when present, call and store on a miss.
    Record,

    // Never call the inner client; a miss is an error. Bit-for-bit reproducible, offline.
    Replay
}

// Record/replay cache (PLAN §8.3). One JSON file per request, keyed by a hash of everything
// that can change the answer, so a replayed run sends exactly the same prompts and gets
// exactly the same responses.
public sealed class CachingClient : ILlmClient
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly ILlmClient? _inner;
    private readonly string _directory;

    public CacheMode Mode { get; }

    public CachingClient(ILlmClient? inner, string directory, CacheMode mode)
    {
        if (mode != CacheMode.Replay && inner is null)
            throw new ArgumentException($"{mode} mode needs a real client", nameof(inner));
        _inner = inner;
        _directory = directory;
        Mode = mode;
        Directory.CreateDirectory(directory);
    }

    public static string Key(LlmRequest r)
    {
        var material = string.Join("\u001f", "v1", r.Model,
            r.Temperature.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            r.MaxOutputTokens.ToString(), r.PromptType, r.System, r.User);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    public string PathFor(string key) => Path.Combine(_directory, key[..2], key + ".json");

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var key = Key(request);
        var path = PathFor(key);

        if (Mode != CacheMode.Live && File.Exists(path))
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllTextAsync(path, ct))
                        ?? throw new LlmException($"corrupt cache entry {path}");
            return entry.Response with { FromCache = true, CacheKey = key };
        }

        if (Mode == CacheMode.Replay)
            throw new LlmException($"replay cache miss for {request.PromptType} prompt (key {key[..12]}); " +
                                   "the prompt differs from the recorded run, or the run was never recorded");

        var response = await _inner!.CompleteAsync(request, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(new CacheEntry(request, response with { FromCache = false }, DateTime.UtcNow), Json), ct);
        File.Move(tmp, path, overwrite: true);
        return response with { CacheKey = key };
    }

    public sealed record CacheEntry(LlmRequest Request, LlmResponse Response, DateTime RecordedUtc);
}
