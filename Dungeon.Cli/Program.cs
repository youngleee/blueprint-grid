using Dungeon.Core;
using Dungeon.Render;

// Usage:
//   dotnet run --project Dungeon.Cli -- play   [--seed N] [--tier 1|2|3] [--sight R]
//   dotnet run --project Dungeon.Cli -- render [--seed N] [--tier 1|2|3]
//   echo "e\ne\nstate" | dotnet run --project Dungeon.Cli -- play --seed 1   (scripted)

var command = args.FirstOrDefault() ?? "help";
var seed = IntOption("--seed", 1);
var tier = IntOption("--tier", 3);
var sight = IntOption("--sight", DungeonWorld.DefaultSightRadius);
var options = tier switch
{
    1 => ScenarioOptions.Tier1,
    2 => ScenarioOptions.Tier2,
    3 => ScenarioOptions.Tier3,
    _ => throw new ArgumentException("--tier must be 1, 2 or 3")
};

switch (command)
{
    case "render":
    {
        var world = new DungeonWorld(ScenarioGenerator.Generate(seed, options), seed, sight);
        Console.WriteLine($"seed {seed}, tier {tier}\n");
        Console.WriteLine(AsciiRenderer.Render(world, reveal: true));
        Console.WriteLine();
        Console.WriteLine(AsciiRenderer.Debug(world));
        Console.WriteLine();
        Console.WriteLine(AsciiRenderer.Legend);
        break;
    }
    case "play":
    {
        var world = new DungeonWorld(ScenarioGenerator.Generate(seed, options), seed, sight);
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
            var result = CommandInterpreter.Execute(world, line);
            Console.WriteLine(result.Output);
            if (result.Quit)
                break;
            if (world.Status != EpisodeStatus.Running)
            {
                Console.WriteLine($"\n=== episode {world.Status.ToString().ToLowerInvariant()} at tick {world.Tick} ===");
                if (interactive)
                    continue;
                break;
            }
        }
        break;
    }
    default:
        Console.WriteLine("commands: play | render   options: --seed N --tier 1|2|3 --sight R");
        break;
}

int IntOption(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}
