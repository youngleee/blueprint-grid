using System;
using System.Net.Http;

namespace GridBlueprint.Model;

// Selects the configured LLM backend without changing the agent.
public static class LlmClientFactory
{
    public static ILlmClient Create()
    {
        var provider = Environment.GetEnvironmentVariable("ADAPTIVE_LLM_PROVIDER") ?? "fake";
        if (!provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
            return new FakeLlmClient();

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY is required for the OpenAI provider");

        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1";
        return new OpenAiLlmClient(new HttpClient(), apiKey, model);
    }
}
