using System;
using System.IO;
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

    public int LlmCallCount => _skillGenerator.CallCount;
    public int InputTokens => _skillGenerator.InputTokens;
    public int OutputTokens => _skillGenerator.OutputTokens;

    public ActionRegistry ActionRegistry { get; } = new();

    private CompositeAction _activeSkill;
    private int _activeSkillStep;
    private bool _secondGoalStarted;
    private bool _skillGenerationFailed;
    private bool _taskFailed;
    private string ArchivePath => Environment.GetEnvironmentVariable("ADAPTIVE_SKILL_ARCHIVE")
        ?? Path.Combine(AppContext.BaseDirectory, "skills.json");
    private readonly LlmSkillGenerator _skillGenerator = new(LlmClientFactory.Create());

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
        SkillArchive.Load(ActionRegistry, ArchivePath);

        Console.WriteLine($"AdaptiveAgent {ID} initialized at {Position} with goal ({GoalX}, {GoalY})");
        Console.WriteLine($"Available actions: {string.Join(", ", ActionRegistry.Actions.Select(action => action.Name))}");
    }

    public void Tick()
    {
        // Move one cardinal step toward the goal on each tick.
        if (GoalReached || _taskFailed)
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
            var remainingX = (int)(GoalX - Position.X);
            var remainingY = (int)(GoalY - Position.Y);
            if (!ActionRegistry.TryGetApplicableComposite(remainingX, remainingY, out var skill))
            {
                if (!_skillGenerationFailed)
                {
                    try
                    {
                        skill = _skillGenerator.Generate(
                            (int)Position.X,
                            (int)Position.Y,
                            GoalX,
                            GoalY,
                            ActionRegistry.Actions.Select(action => action.Name));
                    }
                    catch (Exception generationError)
                    {
                        _skillGenerationFailed = true;
                        Console.WriteLine($"Skill generation failed: {generationError.Message}");
                    }

                    if (!_skillGenerationFailed && !ActionValidator.TryValidate(skill, ActionRegistry, out var error))
                    {
                        Console.WriteLine($"Rejected skill {skill.Name}: {error}");
                        _skillGenerationFailed = true;
                    }
                }

                if (!_skillGenerationFailed)
                {
                    ActionRegistry.Register(skill);
                    SkillArchive.Save(ActionRegistry, ArchivePath);
                    GeneratedSkillCount++;
                    Console.WriteLine($"Registered skill {skill.Name}");
                }
            }
            else
            {
                SkillReuseCount++;
                Console.WriteLine($"Reusing skill {skill.Name}");
            }

            if (!_skillGenerationFailed)
            {
                _activeSkill = skill;
                _activeSkillStep = 0;
                ExecuteSkillStep();
                return;
            }
        }

        var actionName = Position.X != GoalX
            ? (GoalX > Position.X ? "move_right" : "move_left")
            : (GoalY > Position.Y ? "move_down" : "move_up");

        if (!ActionRegistry.TryGet(actionName, out var action))
        {
            Console.WriteLine($"AdaptiveAgent has no action named {actionName}");
            return;
        }

        var nextX = (int)(Position.X + action.DeltaX);
        var nextY = (int)(Position.Y + action.DeltaY);

        if (CanEnter(nextX, nextY))
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
            TryDetour();
        }
    }

    private GridLayer _layer;

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

        var next = new Position(
            (int)(Position.X + step.DeltaX),
            (int)(Position.Y + step.DeltaY));
        if (!CanEnter((int)next.X, (int)next.Y))
        {
            Console.WriteLine($"AdaptiveAgent blocked at {next}");
            _activeSkill = null;
            TryDetour();
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
            _skillGenerationFailed = false;
            Console.WriteLine($"AdaptiveAgent started second goal ({GoalX}, {GoalY})");
            return;
        }

        GoalReached = true;
        CompletionTick = _layer.GetCurrentTick();
        Console.WriteLine("AdaptiveAgent reached its goal");
        Console.WriteLine($"Skills generated: {GeneratedSkillCount}; reused: {SkillReuseCount}; generation calls: {LlmCallCount}");
    }

    private void TryDetour()
    {
        // Reuse only paths whose every step is legal and whose endpoint is this goal.
        var skill = ActionRegistry.CompositeActions.FirstOrDefault(CanReachGoal);
        if (skill != null)
        {
            SkillReuseCount++;
            Console.WriteLine($"Reusing validated detour {skill.Name}");
        }
        else
        {
            if (_skillGenerationFailed)
                return;

            var obstacles = new System.Collections.Generic.List<string>();
            for (var y = 0; y < _layer.Height; y++)
                for (var x = 0; x < _layer.Width; x++)
                    if (!CanEnter(x, y))
                        obstacles.Add($"({x},{y})");

            var feedback = "";
            var map = string.Join("\n", Enumerable.Range(0, _layer.Height).Select(y =>
                $"y={y:D2} " + string.Concat(Enumerable.Range(0, _layer.Width).Select(x =>
                    x == Position.X && y == Position.Y ? 'S' : x == GoalX && y == GoalY ? 'G' : CanEnter(x, y) ? '.' : '#'))));
            for (var attempt = 0; attempt < 3; attempt++)
            {
              try
              {
                skill = _skillGenerator.Generate((int)Position.X, (int)Position.Y, GoalX, GoalY,
                    ActionRegistry.Actions.Select(action => action.Name),
                    $"Blocked-route task. Grid bounds: x=0..{_layer.Width - 1}, y=0..{_layer.Height - 1}. " +
                    $"Blocked cells: {string.Join(", ", obstacles)}. " +
                    $"\nMap (columns x=0..{_layer.Width - 1}, rows labelled y; # blocked, . free, S start, G goal):\n{map}\n" +
                    "Find a short complete detour to the goal. Moving away from the goal is allowed. " +
                    "Choose a free row to cross each wall; check that row is outside the wall's entire blocked y range. " +
                    "Never enter a blocked cell or leave the grid. Steps execute sequentially, one per tick. " + feedback);
                if (!ActionValidator.TryValidate(skill, ActionRegistry, out var error))
                    throw new InvalidOperationException(error);
                if (!CanReachGoal(skill, out error))
                    throw new InvalidOperationException(error);

                ActionRegistry.Register(skill);
                GeneratedSkillCount++;
                SkillArchive.Save(ActionRegistry, ArchivePath);
                Console.WriteLine($"Registered validated detour skill {skill.Name}: {string.Join(", ", skill.Steps)}");
                break;
              }
              catch (Exception error)
              {
                feedback += $"Previous attempt [{string.Join(", ", skill?.Steps ?? Array.Empty<string>())}] rejected: {error.Message}. " +
                    "Recompute the complete path from the original state; stop exactly at the goal.";
                Console.WriteLine(feedback);
                if (attempt == 2)
                {
                    _skillGenerationFailed = true;
                    _taskFailed = true;
                    Console.WriteLine($"TASK FAILED: goal ({GoalX},{GoalY}) not reached; all 3 detour attempts were rejected.");
                    return;
                }
              }
            }
        }

        _activeSkill = skill;
        _activeSkillStep = 0;
    }

    private bool CanReachGoal(CompositeAction skill) => CanReachGoal(skill, out _);

    private bool CanReachGoal(CompositeAction skill, out string error)
    {
        error = $"Path must contain 1 to {CompositeAction.MaxSteps} steps";
        if (skill?.Steps == null || skill.Steps.Count == 0 || skill.Steps.Count > CompositeAction.MaxSteps)
            return false;

        var x = (int)Position.X;
        var y = (int)Position.Y;
        foreach (var name in skill.Steps)
        {
            if (!ActionRegistry.TryGet(name, out var step))
                return false;
            x += step.DeltaX;
            y += step.DeltaY;
            if (!CanEnter(x, y))
            {
                error = $"Path enters blocked or out-of-bounds cell ({x},{y}) via {name}";
                return false;
            }
        }
        error = $"Path ends at ({x},{y}), expected ({GoalX},{GoalY})";
        return x == GoalX && y == GoalY;
    }

    private bool CanEnter(int x, int y)
    {
        return x >= 0 && x < _layer.Width && y >= 0 && y < _layer.Height
            && _layer.IsRoutable(x, y);
    }
}
