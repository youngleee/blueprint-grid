using Dungeon.Agent;

namespace Dungeon.Render;

public sealed record CommandResult(string Output, bool Quit = false);

// Text front end for an agent. Every action goes through ActionSpace, the same path the LLM uses,
// so a human playing in the CLI is subject to exactly the same rules and knowledge limits.
public static class CommandInterpreter
{
    public static string Help(ActionSpace space) =>
        "Actions (one decision each; primitives cost 1 tick unless invalid):\n" +
        string.Join('\n', space.Actions.Select(a => $"  {a.Signature,-28} {a.Description}")) +
        "\nShortcuts: n e s w = move, goto = go_to, take = pick_up. Arguments by space or comma." +
        "\nInspect (free): facts | check <condition> | actions | map | reveal | state | debug | legend | help | quit";

    public static CommandResult Execute(AgentContext ctx, ActionSpace space, string line)
    {
        var trimmed = line.Trim();
        var parts = Tokenize(trimmed);
        if (parts.Count == 0)
            return new CommandResult("");

        var verb = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).ToList();
        var world = ctx.World;

        switch (verb)
        {
            case "quit" or "exit" or "q": return new CommandResult("bye", Quit: true);
            case "help" or "?": return new CommandResult(Help(space));
            case "actions": return new CommandResult(string.Join('\n', space.Actions.Select(a => a.Signature)));
            case "legend": return new CommandResult(AsciiRenderer.Legend);
            case "map": return new CommandResult(AsciiRenderer.Render(world));
            case "reveal": return new CommandResult(AsciiRenderer.Render(world, reveal: true));
            case "state": return new CommandResult(AsciiRenderer.Status(world));
            case "debug": return new CommandResult(AsciiRenderer.Debug(world));
            case "facts": return new CommandResult(FactSet.From(ctx).ToText());
            case "check": return Check(ctx, trimmed[parts[0].Length..].Trim());
        }

        if (verb.Length == 1 && "nesw".Contains(verb))
        {
            args = [verb];
            verb = "move";
        }
        verb = verb switch { "goto" => "go_to", "take" or "pickup" => "pick_up", _ => verb };

        var outcome = space.Execute(ctx, verb, args);
        return new CommandResult(
            $"{outcome}\n\n{AsciiRenderer.Render(world)}\n\n{AsciiRenderer.Status(world)}");
    }

    // Accepts "give food_1 guard_1", "give(food_1, guard_1)" and "give food_1,guard_1".
    public static List<string> Tokenize(string line) =>
        line.Replace('(', ' ').Replace(')', ' ').Replace(',', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static CommandResult Check(AgentContext ctx, string text)
    {
        if (!Condition.TryParse(text, out var condition, out var error))
            return new CommandResult($"error: {error}");
        return new CommandResult($"{condition} = {condition!.Evaluate(FactSet.From(ctx)).ToString().ToLowerInvariant()}");
    }
}
