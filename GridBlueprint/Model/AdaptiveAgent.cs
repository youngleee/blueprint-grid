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

    [PropertyDescription(Name = "SecondGoalX")]
    public int SecondGoalX { get; set; } = -1;

    [PropertyDescription(Name = "SecondGoalY")]
    public int SecondGoalY { get; set; } = -1;

    public bool GoalReached { get; private set; }

    public long CompletionTick { get; private set; } = -1;

    public int GeneratedSkillCount { get; private set; }

    public int SkillReuseCount { get; private set; }

    public ActionRegistry ActionRegistry { get; } = new();

    private CompositeAction _activeSkill;
    private int _activeSkillStep;
    private bool _secondGoalStarted;

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
            AdvanceGoalOrComplete();
            return;
        }

        if (_activeSkill != null)
        {
            ExecuteSkillStep();
            return;
        }

        if (Position.X != GoalX && Position.Y != GoalY)
        {
            var diagonalName = GetDiagonalSkillName();
            if (!ActionRegistry.TryGetComposite(diagonalName, out var skill))
            {
                skill = ManualSkills.MoveDiagonalDownRight;
                if (!ActionValidator.TryValidate(skill, ActionRegistry, out var error))
                {
                    Console.WriteLine($"Rejected skill {skill.Name}: {error}");
                    return;
                }

                ActionRegistry.Register(skill);
                GeneratedSkillCount++;
                Console.WriteLine($"Registered skill {skill.Name}");
            }
            else
            {
                SkillReuseCount++;
                Console.WriteLine($"Reusing skill {skill.Name}");
            }

            _activeSkill = skill;
            _activeSkillStep = 0;
            ExecuteSkillStep();
            return;
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
                AdvanceGoalOrComplete();
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

    private void ExecuteSkillStep()
    {
        if (_activeSkillStep >= _activeSkill.Steps.Count)
        {
            _activeSkill = null;
            return;
        }

        var stepName = _activeSkill.Steps[_activeSkillStep++];
        if (!ActionRegistry.TryGet(stepName, out var step))
        {
            Console.WriteLine($"Skill { _activeSkill.Name } references unknown action {stepName}");
            _activeSkill = null;
            return;
        }

        var next = new Position(Position.X + step.DeltaX, Position.Y + step.DeltaY);
        if (!_layer.IsRoutable(next.X, next.Y))
        {
            Console.WriteLine($"AdaptiveAgent blocked at {next}");
            _activeSkill = null;
            return;
        }

        Position = next;
        _layer.AdaptiveAgentEnvironment.MoveTo(this, next);
        Console.WriteLine($"AdaptiveAgent moved to {Position} via {stepName}");

        if (Position.X == GoalX && Position.Y == GoalY)
            AdvanceGoalOrComplete();
    }

    private void AdvanceGoalOrComplete()
    {
        _activeSkill = null;
        if (!_secondGoalStarted && SecondGoalX >= 0 && SecondGoalY >= 0)
        {
            GoalX = SecondGoalX;
            GoalY = SecondGoalY;
            _secondGoalStarted = true;
            Console.WriteLine($"AdaptiveAgent started second goal ({GoalX}, {GoalY})");
            return;
        }

        GoalReached = true;
        CompletionTick = _layer.GetCurrentTick();
        Console.WriteLine("AdaptiveAgent reached its goal");
    }
}
