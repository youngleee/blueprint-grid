using Dungeon.Agent;
using Dungeon.Mars;
using Mars.Components.Starter;
using Mars.Interfaces.Model;

// Replays a recorded episode inside MARS (for the demo and the PyGame visualizer).
//   dotnet run --project Dungeon.Mars -- --run runs/protocol/test_C_frozen --seed 1001 [--reveal] [--config config.json]
// Start Visualization/main.py first to watch it; without it the replay just prints each decision.
string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

var run = Path.GetFullPath(Option("--run") ?? throw new ArgumentException("--run DIR is required"));
var seed = int.Parse(Option("--seed") ?? throw new ArgumentException("--seed N is required"));
var replay = EpisodeReplay.Replay(run, seed);
if (!replay.MatchesTrace)
    Console.WriteLine($"warning: replay differs from the recorded trace ({replay.Mismatch})");
DungeonLayer.Replay = replay;
DungeonLayer.Reveal = args.Contains("--reveal");

var description = new ModelDescription();
description.AddLayer<DungeonLayer>();
description.AddAgent<ReplayAgent, DungeonLayer>();

var configPath = Path.GetFullPath(Option("--config") ?? Path.Combine(AppContext.BaseDirectory, "config.json"));
var config = SimulationConfig.Deserialize(File.ReadAllText(configPath));
// Layer files are relative to the config file, wherever the command is run from.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
using var starter = SimulationStarter.Start(description, config);
var handle = starter.Run();
Console.WriteLine($"replayed seed {seed}: {replay.Frames.Count} decisions, episode {replay.Status.ToString().ToLowerInvariant()}; MARS ticks {handle.Iterations}");
