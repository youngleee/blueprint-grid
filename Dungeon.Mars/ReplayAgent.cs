using Mars.Interfaces.Agents;
using Mars.Interfaces.Environments;

namespace Dungeon.Mars;

// Advances the replay by one decision per MARS tick and places itself on the agent's cell, so the
// visualizer draws it as the moving dot.
public class ReplayAgent : IAgent<DungeonLayer>, IPositionable
{
    private DungeonLayer _layer = null!;

    public Guid ID { get; set; }
    public Position Position { get; set; } = new(0, 0);

    public void Init(DungeonLayer layer) => _layer = layer;

    public void Tick()
    {
        if (!_layer.Advance())
            return;
        var world = DungeonLayer.Replay!.Frames[_layer.Frame].World;
        Position = new Position(world.AgentPosition.X, _layer.Height - 1 - world.AgentPosition.Y);
    }
}
