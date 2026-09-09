using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GridBlueprint.Model;

// Converts the model's strict JSON response into a composite skill.
public sealed class LlmSkillGenerator
{
    private readonly ILlmClient _client;

    public int CallCount { get; private set; }
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }

    public LlmSkillGenerator(ILlmClient client)
    {
        _client = client;
    }

    public CompositeAction Generate(
        int x,
        int y,
        int goalX,
        int goalY,
        IEnumerable<string> availableActions)
    {
        var prompt = $"State ({x},{y}), goal ({goalX},{goalY}). " +
            $"Available actions: {string.Join(", ", availableActions)}. " +
            "Return a JSON skill with name and steps only.";
        CallCount++;
        var response = _client.GenerateAsync(prompt).GetAwaiter().GetResult();
        InputTokens += response.InputTokens ?? 0;
        OutputTokens += response.OutputTokens ?? 0;
        return JsonSerializer.Deserialize<CompositeAction>(response.Text, new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true })
            ?? throw new JsonException("LLM returned an empty skill");
    }
}
