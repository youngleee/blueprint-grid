using Dungeon.Agent;
using Dungeon.Core;

namespace Dungeon.Render;

public sealed record CommandResult(string Output, bool Quit = false);

// Parses and executes text commands for an agent. Shared by the CLI and its tests;
// later the LLM's chosen actions go through the same syntax.
public static class CommandInterpreter
{
    public const string Help =
        "Primitives (1 tick each unless invalid):\n" +
        "  move N|E|S|W        (also: n e s w)\n" +
        "  wait | look | pick_up\n" +
        "  talk <guard>        give <item> <guard>\n" +
        "  throw <item> <x> <y>\n" +
        "  use <item|none> <door>\n" +
        "Helpers (several ticks, stop early when something new happens):\n" +
        "  goto <id|exit> [distance]    explore\n" +
        "Inspect (free):\n" +
        "  facts | check <condition> | map | reveal | state | debug | legend | help | quit";

    public static CommandResult Execute(AgentContext ctx, string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return new CommandResult("");

        var verb = parts[0].ToLowerInvariant();
        var args = parts[1..];
        var world = ctx.World;

        if (verb.Length == 1 && "nesw".Contains(verb))
        {
            args = [verb];
            verb = "move";
        }

        try
        {
            return verb switch
            {
                "quit" or "exit" or "q" => new CommandResult("bye", Quit: true),
                "help" or "?" => new CommandResult(Help),
                "legend" => new CommandResult(AsciiRenderer.Legend),
                "map" => new CommandResult(AsciiRenderer.Render(world)),
                "reveal" => new CommandResult(AsciiRenderer.Render(world, reveal: true)),
                "state" => new CommandResult(AsciiRenderer.Status(world)),
                "debug" => new CommandResult(AsciiRenderer.Debug(world)),
                "facts" => new CommandResult(FactSet.From(ctx).ToText()),
                "check" => Check(ctx, line.Trim()[parts[0].Length..].Trim()),
                "goto" => Report(ctx, ctx.Decide(c => GoTo(c, args)).ToString()),
                "explore" => Report(ctx, ctx.Decide(c => Navigation.Explore(c)).ToString()),
                _ => Report(ctx, ctx.Decide(c => c.Do(w => Act(w, verb, args))).ToString())
            };
        }
        catch (ArgumentException e)
        {
            return new CommandResult($"error: {e.Message}");
        }
    }

    private static CommandResult Report(AgentContext ctx, string result) =>
        new($"{result}\n\n{AsciiRenderer.Render(ctx.World)}\n\n{AsciiRenderer.Status(ctx.World)}");

    private static CommandResult Check(AgentContext ctx, string text)
    {
        if (!Condition.TryParse(text, out var condition, out var error))
            return new CommandResult($"error: {error}");
        return new CommandResult($"{condition} = {condition!.Evaluate(FactSet.From(ctx)).ToString().ToLowerInvariant()}");
    }

    private static HelperResult GoTo(AgentContext ctx, string[] args)
    {
        var id = Arg(args, 0, "id|exit");
        int? distance = args.Length > 1 ? Int(args, 1, "distance") : null;
        return Navigation.GoTo(ctx, id, distance);
    }

    private static ActionResult Act(DungeonWorld world, string verb, string[] args) => verb switch
    {
        "move" => world.Move(ParseDirection(Arg(args, 0, "direction"))),
        "wait" => world.Wait(),
        "look" => world.Look(),
        "pick_up" or "pickup" or "take" => world.PickUp(),
        "talk" => world.Talk(Arg(args, 0, "guard")),
        "give" => world.Give(Arg(args, 0, "item"), Arg(args, 1, "guard")),
        "throw" => world.Throw(Arg(args, 0, "item"), new Cell(Int(args, 1, "x"), Int(args, 2, "y"))),
        "use" => world.Use(NullIfNone(Arg(args, 0, "item|none")), Arg(args, 1, "door")),
        _ => throw new ArgumentException($"unknown command '{verb}', type help")
    };

    private static Direction ParseDirection(string s) => s.ToUpperInvariant() switch
    {
        "N" or "UP" => Direction.N,
        "E" or "RIGHT" => Direction.E,
        "S" or "DOWN" => Direction.S,
        "W" or "LEFT" => Direction.W,
        _ => throw new ArgumentException($"bad direction '{s}'")
    };

    private static string Arg(string[] args, int i, string name) =>
        i < args.Length ? args[i] : throw new ArgumentException($"missing <{name}>");

    private static int Int(string[] args, int i, string name) =>
        int.TryParse(Arg(args, i, name), out var v) ? v : throw new ArgumentException($"<{name}> must be a number");

    private static string? NullIfNone(string s) => s.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : s;
}
