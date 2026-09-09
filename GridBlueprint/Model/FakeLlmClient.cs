using System.Threading.Tasks;

namespace GridBlueprint.Model;

// Returns deterministic JSON while the real provider is not configured.
public sealed class FakeLlmClient : ILlmClient
{
    public Task<string> GenerateAsync(string prompt)
    {
        return Task.FromResult("{\"name\":\"move_diagonal_down_right\",\"steps\":[\"move_down\",\"move_right\"]}");
    }
}
