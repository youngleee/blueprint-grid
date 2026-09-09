using System.Threading.Tasks;

namespace GridBlueprint.Model;

public interface ILlmClient
{
    Task<string> GenerateAsync(string prompt);
}
