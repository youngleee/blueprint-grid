using System;
using System.Linq;
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

    public ActionRegistry ActionRegistry { get; } = new();

    public void Init(GridLayer layer)
    {
        // Place the agent and add it to MARS's spatial index.
        _layer = layer;
        Position = new Position(StartX, StartY);
        layer.AdaptiveAgentEnvironment.Insert(this);

        // Register the trusted actions available at startup.
        ActionRegistry.Register(PrimitiveActions.MoveUp);
        ActionRegistry.Register(PrimitiveActions.MoveDown);
        ActionRegistry.Register(PrimitiveActions.MoveLeft);
        ActionRegistry.Register(PrimitiveActions.MoveRight);

        Console.WriteLine($"AdaptiveAgent {ID} initialized at {Position} with goal ({GoalX}, {GoalY})");
        Console.WriteLine($"Available actions: {string.Join(", ", ActionRegistry.Actions.Select(action => action.Name))}");
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

        if (Position.X != GoalX && Position.Y != GoalY)
        {
            var diagonalName = GetDiagonalSkillName();
            if (!ActionRegistry.TryGetComposite(diagonalName, out _))
                Console.WriteLine($"No applicable skill named {diagonalName}");
        }

        var actionName = Position.X != GoalX
            ? (GoalX > Position.X ? "move_right" : "move_left")
            : (GoalY > Position.Y ? "move_down" : "move_up");

        if (!ActionRegistry.TryGet(actionName, out var action))
        {
            Console.WriteLine($"AdaptiveAgent has no action named {actionName}");
            return;
        }

        var nextX = Position.X + action.DeltaX;
        var nextY = Position.Y + action.DeltaY;

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

    private string GetDiagonalSkillName()
    {
        var horizontal = GoalX > Position.X ? "right" : "left";
        var vertical = GoalY > Position.Y ? "down" : "up";
        return $"move_diagonal_{vertical}_{horizontal}";
    }
}
