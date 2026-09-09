using System.Threading.Tasks;

namespace GridBlueprint.Model;

// Returns deterministic JSON while the real provider is not configured.
public sealed class FakeLlmClient : ILlmClient
{
    public Task<LlmResponse> GenerateAsync(string prompt)
    {
        return Task.FromResult(new LlmResponse(
            "{\"name\":\"move_diagonal_down_right\",\"steps\":[\"move_down\",\"move_right\"]}",
            null,
            null));
    }
}
