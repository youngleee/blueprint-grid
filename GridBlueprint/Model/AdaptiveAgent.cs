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
        // Place the agent at its configured starting cell.
        Position = new Position(StartX, StartY);
    }

    public void Tick()
    {
        // Movement behavior will be introduced in a later increment.
    }
}
