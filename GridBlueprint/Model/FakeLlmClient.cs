using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace GridBlueprint.Model;

// Returns deterministic JSON while the real provider is not configured.
public sealed class FakeLlmClient : ILlmClient
{
    public Task<LlmResponse> GenerateAsync(string prompt)
    {
        var match = Regex.Match(prompt, @"State \((?<x>-?\d+),(?<y>-?\d+)\), goal \((?<goalX>-?\d+),(?<goalY>-?\d+)\)");
        var horizontal = int.Parse(match.Groups["goalX"].Value) > int.Parse(match.Groups["x"].Value)
            ? "right" : "left";
        var vertical = int.Parse(match.Groups["goalY"].Value) > int.Parse(match.Groups["y"].Value)
            ? "down" : "up";
        // This canned detour is only an offline fixture for the two-wall demo.
        if (prompt.Contains("Blocked-route task"))
            return Task.FromResult(new LlmResponse(
                "{\"name\":\"detour_right_2\",\"steps\":[\"move_down\",\"move_right\",\"move_right\",\"move_up\"]}",
                null, null));
        return Task.FromResult(new LlmResponse(
            $"{{\"name\":\"move_diagonal_{vertical}_{horizontal}\",\"steps\":[\"move_{vertical}\",\"move_{horizontal}\"]}}",
            null,
            null));
    }
}
