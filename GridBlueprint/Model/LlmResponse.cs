namespace GridBlueprint.Model;

public sealed record LlmResponse(string Text, int? InputTokens, int? OutputTokens);
