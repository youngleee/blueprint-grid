namespace Dungeon.Llm;

// Settings come from process environment first, then a local .env file (never committed).
public sealed record LlmSettings(string Provider, string? ApiKey, string Model, double Temperature, string CacheDir)
{
    public const string DefaultModel = "gpt-4.1-mini-2025-04-14";

    public static LlmSettings Load(string? envFile = null)
    {
        var file = envFile is not null && File.Exists(envFile) ? ParseEnvFile(File.ReadAllLines(envFile)) : new();
        string? Get(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : file.GetValueOrDefault(key);

        return new LlmSettings(
            Provider: Get("DUNGEON_LLM_PROVIDER") ?? "openai",
            ApiKey: Get("OPENAI_API_KEY"),
            Model: Get("OPENAI_MODEL") ?? DefaultModel,
            Temperature: double.TryParse(Get("OPENAI_TEMPERATURE"), System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0.0,
            CacheDir: Get("DUNGEON_LLM_CACHE") ?? "cache/llm");
    }

    // KEY=VALUE lines; '#' comments; optional surrounding quotes.
    public static Dictionary<string, string> ParseEnvFile(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            result[line[..eq].Trim()] = value;
        }
        return result;
    }

    // For logs and run.json: never includes the key itself.
    public string Describe() => $"provider={Provider} model={Model} temperature={Temperature} key={(ApiKey is null ? "missing" : "set")}";
}
