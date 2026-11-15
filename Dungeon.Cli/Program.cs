using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Llm;
using Dungeon.Render;
using Dungeon.Skills;

// Usage:
//   dotnet run --project Dungeon.Cli -- play   [--seed N] [--tier 1|2|3] [--sight R] [--skills handwritten|FILE]
//   dotnet run --project Dungeon.Cli -- render [--seed N] [--tier 1|2|3]
//   dotnet run --project Dungeon.Cli -- skills [--file FILE] [--test] [--macro]   (print, validate and test a library)
//   dotnet run --project Dungeon.Cli -- run    --condition A|R|D|RD|B|C|E [--tier 3] [--seeds 1-20] [--max-decisions 40]
//                                              [--llm live|record|replay] [--provider openai|stub] [--out DIR]
//                                              [--max-calls N] [--max-tokens N] [--env FILE] [--skills FILE]
//                                              [--freeze] [--induce-attempts N] [--induce-calls N]
//                                              [--admission tested|valid]   (B, C, E)
//   echo "e\ne\nstate" | dotnet run --project Dungeon.Cli -- play --seed 1   (scripted)

var command = args.FirstOrDefault() ?? "help";

try
{
    switch (command)
    {
        case "render": Render(); break;
        case "play": Play(); break;
        case "run": return await Run();
        case "skills": PrintSkills(); break;
        default:
            Console.WriteLine("commands: play | render | run | skills   (see docs/TESTING.md)");
            break;
    }
    return 0;
}
catch (Exception e) when (e is ArgumentException or SkillFormatException or FileNotFoundException)
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
    var space = StandardActions.Create().WithSkills(LoadSkills(Option("--skills")));
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
    var condition = (Option("--condition") ?? throw new ArgumentException("--condition is required (A, R, D, RD, B, C or E)")).ToUpperInvariant();
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
    ILlmClient? llm = null;
    if (!ExperimentRunner.UsesLlm(condition))
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
        llm = new CachingClient(metered, cacheDir, mode);
        decider = new LlmDecider(llm, settings.Model, settings.Temperature);
    }

    Console.WriteLine($"condition {condition}, tier {tier}, {seeds.Count} seeds, max {maxDecisions} decisions");
    Console.WriteLine($"decider {decider.Name}, {(!ExperimentRunner.UsesLlm(condition) ? "no LLM" : $"{settings.Describe()} provider={provider} cache={mode.ToString().ToLowerInvariant()} ({cacheDir})")}");
    Console.WriteLine($"output {outDir}\n");

    // D / RD: fixed library (default: built-in). B / C / E: starting library (default: empty), e.g. the
    // library.json of a training run together with --freeze for held-out evaluation.
    var induces = ExperimentRunner.Induces(condition);
    var librarySource = Option("--skills") ?? (induces ? "none" : "handwritten");
    if (librarySource is not ("handwritten" or "none") && condition is not ("D" or "RD" or "C" or "E"))
        throw new ArgumentException("--skills only applies to conditions D, RD, C and E");
    var freeze = args.Contains("--freeze");
    var admission = (Option("--admission") ?? "tested") switch
    {
        "tested" => AdmissionMode.Tested,
        "valid" => AdmissionMode.Valid,
        var a => throw new ArgumentException($"--admission must be tested or valid, not '{a}'")
    };
    var inducer = induces && llm is not null
        ? new SkillInducer(llm, settings.Model, settings.Temperature, ExperimentRunner.FormFor(condition), admission: admission)
        : null;
    var summary = await ExperimentRunner.RunAsync(new ExperimentConfig(condition, tier, seeds, maxDecisions, outDir,
        provider, settings.Model, settings.Temperature, mode, cacheDir, budget, LoadSkills(librarySource), librarySource,
        freeze, IntOption("--induce-attempts", 3), IntOption("--induce-calls", 6), Admission: admission),
        decider, Console.WriteLine, inducer: inducer);

    Console.WriteLine($"\nsuccess {summary.Successes}/{summary.Episodes} ({summary.SuccessRate:P0}), " +
                      $"mean decisions {summary.MeanDecisions:F1}, LLM calls {summary.LlmCalls} (cached {summary.CacheHits}), " +
                      $"tokens in {summary.InputTokens} out {summary.OutputTokens}" +
                      (induces ? $"\nskills induced {summary.SkillsInduced} in {summary.InduceCalls} induction calls, final library {summary.FinalLibrarySize}" : ""));
    if (summary.AbortedReason is not null)
    {
        Console.WriteLine($"ABORTED: {summary.AbortedReason}");
        return 1;
    }
    return 0;
}

void PrintSkills()
{
    var file = Option("--file");
    var skills = LoadSkills(file ?? "handwritten");
    var test = args.Contains("--test");
    var form = args.Contains("--macro") ? SkillForm.Macro : SkillForm.ClosedLoop;
    // Each skill is checked against the primitives, helpers and the skills listed before it.
    var space = StandardActions.Create();
    var admitted = new List<Skill>();
    foreach (var s in skills)
    {
        Console.WriteLine($"{s.Name}({string.Join(", ", s.Params.Select(p => $"{p.Name}: {SkillJson.ParamTypes.First(kv => kv.Value == p.Type).Key}"))})");
        Console.WriteLine($"  {SkillActions.Describe(s)}");
        if (test)
        {
            var r = SkillAdmission.Check(s, space, admitted, new ValidationOptions(form));
            Console.WriteLine("  " + r.Feedback().Replace("\n", "\n  "));
            if (r.Accepted)
                admitted.Add(s);
        }
        space.Register(SkillActions.ToAction(s));
        Console.WriteLine();
    }
    if (!test)
        Console.WriteLine(file is null ? HandwrittenSkills.Json() : File.ReadAllText(file));
}

// "handwritten" = embedded library for condition D; otherwise a JSON file with a list of skills.
IReadOnlyList<Skill> LoadSkills(string? source) => source switch
{
    null or "" or "none" => [],
    "handwritten" => HandwrittenSkills.Load(),
    _ => SkillJson.ParseLibrary(File.ReadAllText(source))
};

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
