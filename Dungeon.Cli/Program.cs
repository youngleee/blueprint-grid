using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Llm;
using Dungeon.Render;

// Usage:
//   dotnet run --project Dungeon.Cli -- play   [--seed N] [--tier 1|2|3] [--sight R]
//   dotnet run --project Dungeon.Cli -- render [--seed N] [--tier 1|2|3]
//   dotnet run --project Dungeon.Cli -- run    --condition A|R [--tier 3] [--seeds 1-20] [--max-decisions 40]
//                                              [--llm live|record|replay] [--provider openai|stub] [--out DIR]
//                                              [--max-calls N] [--max-tokens N] [--env FILE]
//   echo "e\ne\nstate" | dotnet run --project Dungeon.Cli -- play --seed 1   (scripted)

var command = args.FirstOrDefault() ?? "help";

try
{
    switch (command)
    {
        case "render": Render(); break;
        case "play": Play(); break;
        case "run": return await Run();
        default:
            Console.WriteLine("commands: play | render | run   (see docs/TESTING.md)");
            break;
    }
    return 0;
}
catch (ArgumentException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

void Render()
{
    var (seed, tier) = (IntOption("--seed", 1), Option("--tier") ?? "3");
    var world = new DungeonWorld(ScenarioGenerator.Generate(seed, ExperimentRunner.TierOptions(tier)), seed,
        IntOption("--sight", DungeonWorld.DefaultSightRadius));
    Console.WriteLine($"seed {seed}, tier {tier}\n");
    Console.WriteLine(AsciiRenderer.Render(world, reveal: true));
    Console.WriteLine();
    Console.WriteLine(AsciiRenderer.Debug(world));
    Console.WriteLine();
    Console.WriteLine(AsciiRenderer.Legend);
}

void Play()
{
    var (seed, tier) = (IntOption("--seed", 1), Option("--tier") ?? "3");
    var sight = IntOption("--sight", DungeonWorld.DefaultSightRadius);
    var world = new DungeonWorld(ScenarioGenerator.Generate(seed, ExperimentRunner.TierOptions(tier)), seed, sight);
    var agent = new AgentContext(world);
    var space = StandardActions.Create();
    var interactive = !Console.IsInputRedirected;
    Console.WriteLine($"seed {seed}, tier {tier}, sight {sight}. Reach X. Type 'help' for commands.\n");
    Console.WriteLine(AsciiRenderer.Render(world));
    Console.WriteLine();
    Console.WriteLine(AsciiRenderer.Status(world));
    while (true)
    {
        if (interactive)
            Console.Write("\n> ");
        var line = Console.ReadLine();
        if (line is null)
            break;
        if (!interactive)
            Console.WriteLine($"\n> {line}");
        var result = CommandInterpreter.Execute(agent, space, line);
        Console.WriteLine(result.Output);
        if (result.Quit)
            break;
        if (world.Status != EpisodeStatus.Running)
        {
            Console.WriteLine($"\n=== episode {world.Status.ToString().ToLowerInvariant()} at tick {world.Tick} ===");
            if (!interactive)
                break;
        }
    }
}

async Task<int> Run()
{
    var condition = (Option("--condition") ?? throw new ArgumentException("--condition is required (A or R)")).ToUpperInvariant();
    if (!ExperimentRunner.Conditions.Contains(condition))
        throw new ArgumentException($"condition '{condition}' is not implemented yet (available: {string.Join(", ", ExperimentRunner.Conditions)})");
    var tier = Option("--tier") ?? "3";
    ExperimentRunner.TierOptions(tier);
    var seeds = ExperimentRunner.ParseSeeds(Option("--seeds") ?? "1-20");
    var maxDecisions = IntOption("--max-decisions", 40);
    var settings = LlmSettings.Load(Option("--env") ?? DefaultEnvFile());
    var provider = Option("--provider") ?? settings.Provider;
    var mode = (Option("--llm") ?? "record") switch
    {
        "live" => CacheMode.Live,
        "record" => CacheMode.Record,
        "replay" => CacheMode.Replay,
        var m => throw new ArgumentException($"--llm must be live, record or replay, not '{m}'")
    };
    var budget = new LlmBudget(IntOption("--max-calls", 2000), IntOption("--max-tokens", 3_000_000));
    var outDir = Option("--out") ?? Path.Combine("runs", $"{DateTime.Now:yyyyMMdd-HHmmss}_{condition}_tier{tier.Replace("tier", "")}");
    var cacheDir = Path.Combine(settings.CacheDir, provider);

    IDecider decider;
    if (condition == "R")
    {
        decider = new RuleBasedDecider();
        provider = "none";
    }
    else
    {
        ILlmClient? network = provider switch
        {
            _ when mode == CacheMode.Replay => null,
            "stub" => new StubLlmClient(),
            "openai" => new OpenAiClient(new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
                settings.ApiKey ?? throw new ArgumentException("OPENAI_API_KEY is not set (put it in .env or the environment)")),
            _ => throw new ArgumentException($"unknown provider '{provider}' (openai or stub)")
        };
        var metered = network is null ? null : new MeteredClient(network, budget);
        decider = new LlmDecider(new CachingClient(metered, cacheDir, mode), settings.Model, settings.Temperature);
    }

    Console.WriteLine($"condition {condition}, tier {tier}, {seeds.Count} seeds, max {maxDecisions} decisions");
    Console.WriteLine($"decider {decider.Name}, {(condition == "R" ? "no LLM" : $"{settings.Describe()} provider={provider} cache={mode.ToString().ToLowerInvariant()} ({cacheDir})")}");
    Console.WriteLine($"output {outDir}\n");

    var summary = await ExperimentRunner.RunAsync(new ExperimentConfig(condition, tier, seeds, maxDecisions, outDir,
        provider, settings.Model, settings.Temperature, mode, cacheDir, budget), decider, Console.WriteLine);

    Console.WriteLine($"\nsuccess {summary.Successes}/{summary.Episodes} ({summary.SuccessRate:P0}), " +
                      $"mean decisions {summary.MeanDecisions:F1}, LLM calls {summary.LlmCalls} (cached {summary.CacheHits}), " +
                      $"tokens in {summary.InputTokens} out {summary.OutputTokens}");
    if (summary.AbortedReason is not null)
    {
        Console.WriteLine($"ABORTED: {summary.AbortedReason}");
        return 1;
    }
    return 0;
}

// .env is looked up in the working directory, then the repository root.
string? DefaultEnvFile()
{
    foreach (var dir in new[] { ".", "..", "../.." })
    {
        var path = Path.Combine(dir, ".env");
        if (File.Exists(path))
            return path;
    }
    return null;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

int IntOption(string name, int fallback) =>
    Option(name) is { } v ? int.TryParse(v, out var n) ? n : throw new ArgumentException($"{name} must be a number") : fallback;
