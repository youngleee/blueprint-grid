using System.Text;

namespace Dungeon.Agent;

// The choose_action prompt (PLAN §8.2). The system part is fixed per action set, so it only
// changes when the library grows; the user part is the current facts plus a short history.
public static class Prompts
{
    public const string ChooseActionType = "choose_action";
    public const int HistoryLength = 6;

    public static string ChooseActionSystem(ActionSpace space)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You control an agent in a grid dungeon. Goal: reach the exit cell.");
        sb.AppendLine("You only know what the agent has seen. Coordinates are (x,y); x grows east, y grows south.");
        sb.AppendLine("Guards block corridors. A guard that notices you becomes alert; if it stays alert near you too long it turns hostile and you lose.");
        sb.AppendLine("Calm, bribed or distracted guards let you pass. Closed doors block movement and sight.");
        sb.AppendLine("Outcomes of talk, give, throw, lockpicks and pushing doors are uncertain. Failed attempts cost time.");
        sb.AppendLine("You have a limited number of decisions. Each action below is one decision.");
        sb.AppendLine();
        sb.AppendLine("Available actions:");
        foreach (var a in space.Actions)
            sb.AppendLine($"- {a.Signature}: {a.Description}");
        sb.AppendLine();
        sb.AppendLine("Reply with one JSON object and nothing else:");
        sb.AppendLine("{\"action\": \"<name>\", \"args\": [<arguments as strings>], \"reason\": \"<one short sentence>\"}");
        sb.AppendLine("Examples: {\"action\":\"explore\",\"args\":[],\"reason\":\"nothing useful in sight\"}");
        sb.Append("{\"action\":\"give\",\"args\":[\"food_2\",\"guard_1\"],\"reason\":\"distract the guard to pass\"}");
        return sb.ToString();
    }

    public static string ChooseActionUser(FactSet facts, IReadOnlyList<string> history, int decisionsLeft)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Decisions left: {decisionsLeft}");
        sb.AppendLine();
        sb.AppendLine("Current situation:");
        sb.AppendLine(facts.ToText());
        if (history.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Your last {Math.Min(history.Count, HistoryLength)} decisions (oldest first):");
            foreach (var h in history.TakeLast(HistoryLength))
                sb.AppendLine($"- {h}");
        }
        sb.AppendLine();
        sb.Append("Choose the next action.");
        return sb.ToString();
    }
}
