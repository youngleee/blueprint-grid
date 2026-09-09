using System.Threading.Tasks;

namespace GridBlueprint.Model;

public interface ILlmClient
{
    Task<LlmResponse> GenerateAsync(string prompt);
}
