namespace Dungeon.Llm;

public sealed record LlmRequest(
    string Model,
    double Temperature,
    string PromptType,
    string System,
    string User,
    int MaxOutputTokens = 300);

public sealed record LlmResponse(
    string Text,
    int InputTokens,
    int OutputTokens,
    bool FromCache = false,
    string? CacheKey = null,
    double LatencyMs = 0);

public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}

public class LlmException(string message, Exception? inner = null) : Exception(message, inner);
