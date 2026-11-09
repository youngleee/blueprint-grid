using System.Text.Json;

namespace Dungeon.Agent;

public sealed record Decision(string Action, IReadOnlyList<string> Args, string Reason)
{
    public override string ToString() =>
        Args.Count == 0 ? Action : $"{Action}({string.Join(",", Args)})";
}

// Parses the model's JSON reply {"action": ..., "args": [...], "reason": ...}.
// Lenient about shape (code fences, numbers as args, args as one string) and strict about
// content: validation of names and arguments is left to ActionSpace, which reports invalid.
public static class DecisionParser
{
    public static bool TryParse(string text, out Decision decision, out string error)
    {
        decision = new Decision("", [], "");
        var json = StripFences(text.Trim());
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "reply must be a JSON object";
                return false;
            }
            if (!TryGetString(root, "action", out var action) || string.IsNullOrWhiteSpace(action))
            {
                error = "missing \"action\"";
                return false;
            }

            var args = new List<string>();
            if (Find(root, "args") is { } a)
            {
                switch (a.ValueKind)
                {
                    case JsonValueKind.Array:
                        foreach (var e in a.EnumerateArray())
                            args.Add(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText());
                        break;
                    case JsonValueKind.String:
                        args.AddRange(a.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        break;
                    case JsonValueKind.Null:
                        break;
                    default:
                        error = "\"args\" must be a list";
                        return false;
                }
            }

            TryGetString(root, "reason", out var reason);
            decision = new Decision(action.Trim().ToLowerInvariant(), args, reason ?? "");
            error = "";
            return true;
        }
        catch (JsonException e)
        {
            error = $"reply is not valid JSON: {e.Message}";
            return false;
        }
    }

    private static string StripFences(string s)
    {
        if (!s.StartsWith("```"))
            return s;
        var start = s.IndexOf('\n');
        var end = s.LastIndexOf("```", StringComparison.Ordinal);
        return start >= 0 && end > start ? s[(start + 1)..end].Trim() : s.Trim('`');
    }

    private static JsonElement? Find(JsonElement root, string name)
    {
        foreach (var p in root.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        return null;
    }

    private static bool TryGetString(JsonElement root, string name, out string? value)
    {
        value = Find(root, name) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;
        return value is not null;
    }
}
