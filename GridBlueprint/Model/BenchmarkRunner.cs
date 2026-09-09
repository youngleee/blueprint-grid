using System;
using System.Collections.Generic;
using System.IO;

namespace GridBlueprint.Model;

public static class BenchmarkRunner
{
    private static readonly BenchmarkTask[] Tasks =
    {
        new("acquire_down_right", 1, 1, 2, 2),
        new("transfer_down_right", 2, 2, 3, 3),
        new("acquire_down_left", 3, 1, 2, 2),
        new("transfer_down_left", 2, 2, 1, 3),
        new("acquire_up_right", 1, 3, 2, 2),
        new("transfer_up_right", 2, 2, 3, 1),
        new("acquire_up_left", 3, 3, 2, 2),
        new("transfer_up_left", 2, 2, 1, 1),
        new("cardinal_right", 1, 2, 3, 2),
        new("cardinal_up", 2, 3, 2, 1)
    };

    public static void Run()
    {
        var results = new List<BenchmarkResult>();
        RunCondition("fixed", false, false, results);
        RunCondition("non_persistent", true, false, results);
        RunCondition("persistent", true, true, results);

        var path = Path.Combine(Directory.GetCurrentDirectory(), "benchmark-results.csv");
        var lines = new List<string> { BenchmarkResult.Header };
        foreach (var result in results)
            lines.Add(result.ToCsv());

        File.WriteAllLines(path, lines);
        Console.WriteLine($"Benchmark results written to {path}");
    }

    private static void RunCondition(
        string condition,
        bool allowsGeneration,
        bool persistsSkills,
        ICollection<BenchmarkResult> results)
    {
        var registry = persistsSkills ? CreateRegistry() : null;
        var generator = new LlmSkillGenerator(new FakeLlmClient());

        foreach (var task in Tasks)
        {
            var taskRegistry = registry ?? CreateRegistry();
            results.Add(RunTask(condition, task, taskRegistry, generator, allowsGeneration));
        }
    }

    private static BenchmarkResult RunTask(
        string condition,
        BenchmarkTask task,
        ActionRegistry registry,
        LlmSkillGenerator generator,
        bool allowsGeneration)
    {
        var x = task.StartX;
        var y = task.StartY;
        var primitiveSteps = 0;
        var highLevelActions = 0;
        var generatedSkills = 0;
        var reusedSkills = 0;
        var redundantGenerations = 0;
        var callsBefore = generator.CallCount;

        while (x != task.GoalX || y != task.GoalY)
        {
            var remainingX = task.GoalX - x;
            var remainingY = task.GoalY - y;
            if (allowsGeneration && remainingX != 0 && remainingY != 0)
            {
                if (!registry.TryGetApplicableComposite(remainingX, remainingY, out var skill))
                {
                    skill = generator.Generate(x, y, task.GoalX, task.GoalY, PrimitiveNames);
                    if (!ActionValidator.TryValidate(skill, registry, out _))
                    {
                        redundantGenerations++;
                        return CreateResult(false);
                    }

                    registry.Register(skill);
                    generatedSkills++;
                }
                else
                {
                    reusedSkills++;
                }

                highLevelActions++;
                foreach (var stepName in skill.Steps)
                {
                    registry.TryGet(stepName, out var step);
                    x += step.DeltaX;
                    y += step.DeltaY;
                    primitiveSteps++;
                }

                continue;
            }

            x += Math.Sign(remainingX);
            y += remainingX == 0 ? Math.Sign(remainingY) : 0;
            primitiveSteps++;
        }

        return CreateResult(true);

        BenchmarkResult CreateResult(bool success)
        {
            return new BenchmarkResult(
                condition,
                task.Name,
                success,
                primitiveSteps,
                highLevelActions,
                generatedSkills,
                reusedSkills,
                redundantGenerations,
                generator.CallCount - callsBefore,
                generator.InputTokens,
                generator.OutputTokens);
        }
    }

    private static ActionRegistry CreateRegistry()
    {
        var registry = new ActionRegistry();
        registry.Register(PrimitiveActions.MoveUp);
        registry.Register(PrimitiveActions.MoveDown);
        registry.Register(PrimitiveActions.MoveLeft);
        registry.Register(PrimitiveActions.MoveRight);
        return registry;
    }

    private static readonly string[] PrimitiveNames =
        { "move_up", "move_down", "move_left", "move_right" };

    private sealed record BenchmarkTask(string Name, int StartX, int StartY, int GoalX, int GoalY);

    private sealed record BenchmarkResult(
        string Condition,
        string Task,
        bool Success,
        int PrimitiveSteps,
        int HighLevelActions,
        int GeneratedSkills,
        int ReusedSkills,
        int RedundantGenerations,
        int LlmCalls,
        int InputTokens,
        int OutputTokens)
    {
        public const string Header =
            "condition,task,success,primitive_steps,high_level_actions,generated_skills,reused_skills,redundant_generations,llm_calls,input_tokens,output_tokens";

        public string ToCsv()
        {
            return $"{Condition},{Task},{Success},{PrimitiveSteps},{HighLevelActions},{GeneratedSkills},{ReusedSkills},{RedundantGenerations},{LlmCalls},{InputTokens},{OutputTokens}";
        }
    }
}
