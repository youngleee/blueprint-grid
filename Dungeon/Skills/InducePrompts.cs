using System.Text;
using Dungeon.Agent;

namespace Dungeon.Skills;

// The induce_skill prompt (PLAN §6.3, §8.2). The system part teaches the skill language and how
// skills are checked; the user part describes the situation that triggered induction.
public static class InducePrompts
{
    public const string PromptType = "induce_skill";
    public const int MaxOutputTokens = 1500;

    // Format examples for tasks other than guards and doors, so they show the language without
    // giving away a solution. Both pass the validator (tested).
    public const string ExampleFetch = """
        {"name":"fetch_item","description":"Walk to a seen item and pick it up","params":[{"name":"it","type":"item"}],"pre":["known(it)","state(it) == on_floor"],"effect":["has(it)"],"body":[{"until":"has(it)","max":3,"do":[{"call":"go_to","args":["it"]},{"if":"not has(it)","then":[{"call":"pick_up"}]}]}]}
        """;

    public const string ExampleFindExit = """
        {"name":"find_exit","description":"Explore until the exit is seen, then walk to it","params":[],"pre":["not at_exit()"],"effect":["at_exit()"],"body":[{"until":"known(exit)","max":5,"do":[{"call":"explore"}]},{"if":"known(exit)","then":[{"call":"go_to","args":["exit"]}]}]}
        """;

    public const string ExampleMacro = """
        {"name":"fetch_food_2","description":"Walk to food_2 and pick it up","params":[],"pre":["known(food_2)"],"effect":["has(food_2)"],"body":[{"call":"go_to","args":["food_2"]},{"call":"pick_up"}]}
        """;

    public static string System(ActionSpace space, SkillForm form, ScenarioTestOptions tests)
    {
        var macro = form == SkillForm.Macro;
        var sb = new StringBuilder();
        sb.AppendLine(macro
            ? "You write macros for an agent in a grid dungeon. A macro is a fixed list of actions, saved under a name so the agent can later run it as one action."
            : "You design reusable skills for an agent in a grid dungeon. A skill is a small JSON program that the agent can then use as one action, on any entity of the right kind.");
        sb.AppendLine("Goal of the agent: reach the exit. Guards block corridors; calm, bribed or distracted guards let it pass, and a guard that stays alert near the agent too long turns hostile and the agent loses. Closed doors block movement. Outcomes of talk, give, throw, lockpicks and pushing doors are uncertain.");
        sb.AppendLine();
        sb.AppendLine("Actions it can call:");
        foreach (var a in space.Actions)
            sb.AppendLine($"- {a.Signature}: {a.Description}");
        sb.AppendLine();
        sb.AppendLine("Format (one JSON object):");
        sb.AppendLine("{\"name\": \"lowercase_name\", \"description\": \"...\", \"params\": [{\"name\": \"g\", \"type\": \"guard\"}], \"pre\": [conditions], \"effect\": [conditions], \"body\": [statements]}");
        sb.AppendLine("Parameter types: guard, door, item, target, dir, int.");
        sb.AppendLine("Statements:");
        sb.AppendLine("  {\"call\": \"<action>\", \"args\": [...]}");
        if (!macro)
        {
            sb.AppendLine("  {\"if\": \"<condition>\", \"then\": [...], \"else\": [...]}   (else is optional)");
            sb.AppendLine("  {\"until\": \"<condition>\", \"max\": N, \"do\": [...]}        (checks the condition before each pass; N at most 6)");
            sb.AppendLine("  {\"repeat\": N, \"do\": [...]}                              (N at most 6)");
            sb.AppendLine("  {\"fail\": \"<reason>\"}");
        }
        sb.AppendLine(macro
            ? "Arguments: specific ids from the situation (e.g. guard_1, food_2), numbers, \"none\", \"exit\", pos(x) (two arguments: x and y of x)."
            : "Arguments: a parameter name, a number, \"none\", \"exit\", pos(x) (two arguments: x and y of x's position), held(t) (id of a held item of type t: food, coin, lockpick, key, tool), key_for(d) (id of the held key that opens door d).");
        sb.AppendLine("Conditions, combined with and / or / not and parentheses:");
        sb.AppendLine("  visible(x), known(x), adjacent(x), has(x) (an item id or type), has_key(d), at_exit()");
        sb.AppendLine("  state(x) == s, type(x) == t, dist(x) <= n, count(t) >= n, state(x) in [a, b]");
        sb.AppendLine("  guard states: idle, alert, calm, distracted, bribed, hostile; door states: open, closed; item states: held, on_floor");
        sb.AppendLine("  types: guard, locked, jammed, key, food, coin, lockpick, tool. x may also be exit.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        if (macro)
        {
            sb.AppendLine("- No parameters and no if / until / repeat / fail: only a fixed list of calls.");
            sb.AppendLine("- Use the specific ids of the current situation; pre and effect may name them too.");
        }
        else
        {
            sb.AppendLine("- Use parameters, never specific ids like guard_1: the skill must work for any entity of its kind.");
            sb.AppendLine("- Include at least one if or until that checks the situation, so the skill reacts to what actually happens.");
            sb.AppendLine("- Every parameter must be used. At most 40 calls in the worst case.");
        }
        sb.AppendLine("- \"pre\" says when the skill can be used; \"effect\" says what it achieves and is checked afterwards.");
        sb.AppendLine("- The effect must be progress: a guard calm, bribed or distracted, a door open, an item held, or the exit reached.");
        sb.AppendLine("- The name must be new: not an action above and not a skill already in the library.");
        sb.AppendLine("- Each call runs to completion before the next; conditions always look at the current situation.");
        sb.AppendLine();
        sb.AppendLine($"Before it is accepted, it runs in {tests.Count} generated test rooms: {(macro ? "the entities it names" : "one entity per parameter")} in a corridor, " +
                      "the agent 1 to 3 cells away, holding a random selection of food, coin and lockpick. " +
                      $"It is accepted if the effect is reached in at least {tests.Required} rooms and the agent is never caught.");
        sb.AppendLine();
        sb.AppendLine("Example for another task:");
        sb.AppendLine((macro ? ExampleMacro : ExampleFetch).Trim());
        if (!macro)
            sb.AppendLine(ExampleFindExit.Trim());
        sb.AppendLine();
        sb.Append("Reply with exactly one JSON object and nothing else.");
        return sb.ToString();
    }

    public static string User(InductionRequest r, IReadOnlyList<Skill> library, SkillForm form,
        IReadOnlyList<(string Reply, string Feedback)> previous)
    {
        var t = r.Target;
        var sb = new StringBuilder();
        sb.AppendLine($"The agent needs a new {(form == SkillForm.Macro ? "macro" : "skill")} ({r.Trigger}).");
        sb.AppendLine($"Obstacle: {t.Id}, a {(t.Kind == EntityKind.Door ? $"{t.Subtype} door" : "guard")}, currently {t.State}.");
        sb.AppendLine();
        sb.AppendLine("Current situation:");
        sb.AppendLine(r.Facts.ToText());
        if (r.History.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Recent decisions (oldest first):");
            foreach (var h in r.History.TakeLast(Prompts.HistoryLength))
                sb.AppendLine($"- {h}");
        }
        sb.AppendLine();
        sb.AppendLine(library.Count == 0 ? "Skills already in the library: none." : "Skills already in the library:");
        foreach (var s in library)
            sb.AppendLine($"- {SkillActions.ToAction(s).Signature}: {SkillActions.Describe(s)}");
        sb.AppendLine();
        sb.Append(form == SkillForm.Macro
            ? $"Write one macro that gets the agent past {t.Id} in this situation."
            : $"Write one new skill with a parameter of type {(t.Kind == EntityKind.Door ? "door" : "guard")} that gets the agent past this kind of obstacle in general, not only {t.Id}.");

        for (var i = 0; i < previous.Count; i++)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"Your attempt {i + 1}:");
            sb.AppendLine(previous[i].Reply.Trim());
            sb.AppendLine("Result:");
            sb.AppendLine(previous[i].Feedback.Trim());
        }
        if (previous.Count > 0)
        {
            sb.AppendLine();
            sb.Append("Write a corrected version that fixes these problems.");
        }
        return sb.ToString();
    }
}
