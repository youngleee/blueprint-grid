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

    [PropertyDescription(Name = "GoalX")]
    public int GoalX { get; set; }

    [PropertyDescription(Name = "GoalY")]
    public int GoalY { get; set; }

    public bool GoalReached { get; private set; }

    public long CompletionTick { get; private set; } = -1;

    public void Init(GridLayer layer)
    {
        // Place the agent and add it to MARS's spatial index.
        _layer = layer;
        Position = new Position(StartX, StartY);
        layer.AdaptiveAgentEnvironment.Insert(this);
        Console.WriteLine($"AdaptiveAgent {ID} initialized at {Position} with goal ({GoalX}, {GoalY})");
    }

    public void Tick()
    {
        // Move one cardinal step toward the goal on each tick.
        if (GoalReached)
            return;

        if (Position.X == GoalX && Position.Y == GoalY)
        {
            GoalReached = true;
            CompletionTick = _layer.GetCurrentTick();
            return;
        }

        var nextX = Position.X;
        var nextY = Position.Y;

        if (nextX != GoalX)
            nextX += Math.Sign(GoalX - nextX);
        else
            nextY += Math.Sign(GoalY - nextY);

        if (_layer.IsRoutable(nextX, nextY))
        {
            Position = new Position(nextX, nextY);
            _layer.AdaptiveAgentEnvironment.MoveTo(this, new Position(nextX, nextY));
            Console.WriteLine($"AdaptiveAgent moved to {Position}");

            if (Position.X == GoalX && Position.Y == GoalY)
            {
                GoalReached = true;
                CompletionTick = _layer.GetCurrentTick();
                Console.WriteLine("AdaptiveAgent reached its goal");
            }
        }
        else
        {
            Console.WriteLine($"AdaptiveAgent blocked at ({nextX}, {nextY})");
        }
    }

    private GridLayer _layer;
}
