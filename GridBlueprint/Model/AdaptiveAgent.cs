using System;
using Mars.Interfaces.Agents;
using Mars.Interfaces.Annotations;
using Mars.Interfaces.Environments;

namespace GridBlueprint.Model;

public class AdaptiveAgent : IAgent<GridLayer>, IPositionable
{
    public Guid ID { get; set; }

    public Position Position { get; set; }

    // MARS loads these values from the configured agent CSV before initialization.
    [PropertyDescription(Name = "StartX")]
    public int StartX { get; set; }

    [PropertyDescription(Name = "StartY")]
    public int StartY { get; set; }

    public void Init(GridLayer layer)
    {
        // Place the agent and add it to MARS's spatial index.
        Position = new Position(StartX, StartY);
        layer.AdaptiveAgentEnvironment.Insert(this);
        Console.WriteLine($"AdaptiveAgent {ID} initialized at {Position}");
    }

    public void Tick()
    {
        // Movement behavior will be introduced in a later increment.
    }
}
