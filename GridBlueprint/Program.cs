using System;
using System.IO;
using GridBlueprint.Model;
using Mars.Components.Starter;
using Mars.Interfaces.Model;

namespace GridBlueprint;

internal static class Program
{
    private static void Main(string[] args)
    {
        EnvironmentFile.Load(".env");
        if (args.Length == 1 && args[0] == "--benchmark")
        {
            BenchmarkRunner.Run();
            return;
        }

        // Create a new model description and add model components to it
        var description = new ModelDescription();
        description.AddLayer<GridLayer>();
        description.AddAgent<SimpleAgent, GridLayer>();
        description.AddAgent<ComplexAgent, GridLayer>();
        description.AddAgent<AdaptiveAgent, GridLayer>();
        description.AddAgent<HelperAgent, GridLayer>();

        // Use the default blueprint unless a scenario file is supplied.
        var configPath = args.Length == 0 ? "config.json" : args[0];
        if (!File.Exists(configPath))
            configPath = Path.Combine(AppContext.BaseDirectory, configPath);

        var file = File.ReadAllText(configPath);
        var config = SimulationConfig.Deserialize(file);

        // Couple model description and simulation configuration
        var starter = SimulationStarter.Start(description, config);

        // Run the simulation
        var handle = starter.Run();

        // Close the program
        Console.WriteLine("Simulation finished. Iterations: " + handle.Iterations);
        starter.Dispose();
    }
}
