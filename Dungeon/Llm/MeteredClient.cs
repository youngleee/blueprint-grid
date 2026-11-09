namespace Dungeon.Llm;

public sealed record LlmBudget(int MaxCalls = 5000, long MaxTokens = 5_000_000);

public sealed class BudgetExceededException(string message) : LlmException(message);

// Enforces hard caps on real (billed) API usage (PLAN §8.4). Wrap the network client with it
// and put the cache outside: cached answers are free and never blocked, so a replay of a run
// that hit the budget still replays.
//
//   CachingClient( MeteredClient( OpenAiClient ) )
public sealed class MeteredClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly LlmBudget _budget;
    private readonly object _lock = new();

    public int Calls { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long Tokens => InputTokens + OutputTokens;

    public MeteredClient(ILlmClient inner, LlmBudget budget)
    {
        _inner = inner;
        _budget = budget;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (Calls >= _budget.MaxCalls)
                throw new BudgetExceededException($"LLM call budget of {_budget.MaxCalls} reached");
            if (Tokens >= _budget.MaxTokens)
                throw new BudgetExceededException($"LLM token budget of {_budget.MaxTokens} reached");
        }

        var response = await _inner.CompleteAsync(request, ct);
        lock (_lock)
        {
            Calls++;
            InputTokens += response.InputTokens;
            OutputTokens += response.OutputTokens;
        }
        return response;
    }
}
