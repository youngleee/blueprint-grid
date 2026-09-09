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
        IEnumerable<string> availableActions,
        string taskContext = "")
    {
        var prompt = $"State ({x},{y}), goal ({goalX},{goalY}). " +
            $"Available actions: {string.Join(", ", availableActions)}. " +
            "Movement deltas: move_up=(0,-1), move_down=(0,1), move_left=(-1,0), move_right=(1,0). " +
            $"Required net displacement: dx={goalX - x}, dy={goalY - y}. " +
            "Before returning, simulate each step from the start and count the deltas: the endpoint must equal the goal exactly. " +
            "Return JSON only: {\"name\":\"reusable_skill_name\",\"steps\":[\"move_right\",\"move_down\"]}. " +
            $"Use only the listed primitives, with 1 to {CompositeAction.MaxSteps} steps. Use a descriptive name without absolute coordinates. " + taskContext;
        System.Console.WriteLine($"Generating skill with {_client.GetType().Name}. Prompt: {prompt}");
        CallCount++;
        var response = _client.GenerateAsync(prompt).GetAwaiter().GetResult();
        System.Console.WriteLine($"Candidate skill: {response.Text}");
        InputTokens += response.InputTokens ?? 0;
        OutputTokens += response.OutputTokens ?? 0;
        return JsonSerializer.Deserialize<CompositeAction>(response.Text, new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true })
            ?? throw new JsonException("LLM returned an empty skill");
    }
}
