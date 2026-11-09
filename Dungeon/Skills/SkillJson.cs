using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dungeon.Agent;

namespace Dungeon.Skills;

public sealed class SkillFormatException(string message) : Exception(message);

// JSON <-> Skill. Checks shape only (keys, types, condition syntax); whether a skill makes sense
// is the validator's job. Error messages name the exact location, because in week 8 they are
// fed back to the LLM verbatim.
public static partial class SkillJson
{
    public static readonly IReadOnlyDictionary<string, ParamType> ParamTypes = new Dictionary<string, ParamType>
    {
        ["guard"] = ParamType.Guard,
        ["door"] = ParamType.Door,
        ["item"] = ParamType.Item,
        ["target"] = ParamType.Target,
        ["dir"] = ParamType.Direction,
        ["int"] = ParamType.Int
    };

    // pos(x) → x y of x's last-known position · held(t) → id of a held item of type or id t
    // key_for(d) → id of the held key that opens door d
    public static readonly IReadOnlyDictionary<string, int> ArgFunctions = new Dictionary<string, int>
    {
        ["pos"] = 1, ["held"] = 1, ["key_for"] = 1
    };

    private static readonly HashSet<string> Reserved = ["exit", "none", "true", "false", "and", "or", "not", "in"];
    private static readonly string[] StatementKeys = ["call", "if", "until", "repeat", "fail"];

    public static Skill Parse(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new SkillFormatException($"not valid JSON: {e.Message}");
        }
        return ParseSkill(node, "skill");
    }

    public static bool TryParse(string json, out Skill? skill, out string error)
    {
        try
        {
            skill = Parse(json);
            error = "";
            return true;
        }
        catch (SkillFormatException e)
        {
            skill = null;
            error = e.Message;
            return false;
        }
    }

    public static List<Skill> ParseLibrary(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new SkillFormatException($"not valid JSON: {e.Message}");
        }
        if (node is not JsonArray array)
            throw new SkillFormatException("library must be a JSON array of skills");
        var skills = array.Select((n, i) => ParseSkill(n, $"skills[{i}]")).ToList();
        var duplicate = skills.GroupBy(s => s.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new SkillFormatException($"skill name '{duplicate.Key}' appears twice");
        return skills;
    }

    private static Skill ParseSkill(JsonNode? node, string path)
    {
        var obj = Object(node, path);
        Keys(obj, path, ["name", "description", "params", "pre", "effect", "body"]);

        var name = Identifier(Required(obj, "name", path), $"{path}.name");
        if (name.Length > 40)
            throw new SkillFormatException($"{path}.name: at most 40 characters");
        var description = obj["description"] is null ? "" : String(obj["description"], $"{path}.description");

        var ps = new List<SkillParam>();
        var paramsArray = Array(Required(obj, "params", path), $"{path}.params");
        for (var i = 0; i < paramsArray.Count; i++)
        {
            var p = $"{path}.params[{i}]";
            var po = Object(paramsArray[i], p);
            Keys(po, p, ["name", "type"]);
            var pname = Identifier(Required(po, "name", p), $"{p}.name");
            if (Reserved.Contains(pname))
                throw new SkillFormatException($"{p}.name: '{pname}' is a reserved word");
            if (ps.Any(x => x.Name == pname))
                throw new SkillFormatException($"{p}.name: duplicate parameter '{pname}'");
            var tname = String(Required(po, "type", p), $"{p}.type");
            if (!ParamTypes.TryGetValue(tname, out var type))
                throw new SkillFormatException($"{p}.type: unknown type '{tname}' (use {string.Join(", ", ParamTypes.Keys)})");
            ps.Add(new SkillParam(pname, type));
        }

        var pre = Conditions(Required(obj, "pre", path), $"{path}.pre");
        var effect = Conditions(Required(obj, "effect", path), $"{path}.effect");
        var body = Block(Required(obj, "body", path), $"{path}.body");
        if (body.Count == 0)
            throw new SkillFormatException($"{path}.body: must contain at least one statement");

        return new Skill(name, description, ps, pre, effect, body);
    }

    private static List<SkillCondition> Conditions(JsonNode node, string path) =>
        Array(node, path).Select((n, i) => Cond(n, $"{path}[{i}]")).ToList();

    private static SkillCondition Cond(JsonNode? node, string path)
    {
        var text = String(node, path);
        if (!Condition.TryParse(text, out var parsed, out var error))
            throw new SkillFormatException($"{path}: {error}");
        return new SkillCondition(text, parsed!);
    }

    private static List<SkillStmt> Block(JsonNode? node, string path) =>
        Array(node, path).Select((n, i) => Statement(n, $"{path}[{i}]")).ToList();

    private static SkillStmt Statement(JsonNode? node, string path)
    {
        var obj = Object(node, path);
        var kinds = StatementKeys.Where(k => obj.ContainsKey(k)).ToList();
        if (kinds.Count != 1)
            throw new SkillFormatException($"{path}: a statement needs exactly one of {string.Join(", ", StatementKeys)}" +
                                           (kinds.Count > 1 ? $" (found {string.Join(", ", kinds)})" : ""));

        switch (kinds[0])
        {
            case "call":
            {
                Keys(obj, path, ["call", "args"]);
                var action = Identifier(obj["call"]!, $"{path}.call");
                var args = new List<ArgExpr>();
                if (obj["args"] is { } a)
                {
                    var arr = Array(a, $"{path}.args");
                    for (var i = 0; i < arr.Count; i++)
                        args.Add(Arg(arr[i], $"{path}.args[{i}]"));
                }
                return new CallStmt(action, args);
            }
            case "if":
            {
                Keys(obj, path, ["if", "then", "else"]);
                return new IfStmt(Cond(obj["if"], $"{path}.if"),
                    Block(Required(obj, "then", path), $"{path}.then"),
                    obj["else"] is { } e ? Block(e, $"{path}.else") : []);
            }
            case "until":
            {
                Keys(obj, path, ["until", "max", "do"]);
                var body = Block(Required(obj, "do", path), $"{path}.do");
                if (body.Count == 0)
                    throw new SkillFormatException($"{path}.do: must contain at least one statement");
                return new UntilStmt(Cond(obj["until"], $"{path}.until"), PositiveInt(Required(obj, "max", path), $"{path}.max"), body);
            }
            case "repeat":
            {
                Keys(obj, path, ["repeat", "do"]);
                var body = Block(Required(obj, "do", path), $"{path}.do");
                if (body.Count == 0)
                    throw new SkillFormatException($"{path}.do: must contain at least one statement");
                return new RepeatStmt(PositiveInt(obj["repeat"]!, $"{path}.repeat"), body);
            }
            default:
                Keys(obj, path, ["fail"]);
                return new FailStmt(String(obj["fail"], $"{path}.fail"));
        }
    }

    private static ArgExpr Arg(JsonNode? node, string path)
    {
        if (node is JsonValue v && v.TryGetValue<int>(out var number))
            return new NameArg(number.ToString());
        var text = String(node, path).Trim();
        var m = FunctionCall().Match(text);
        if (m.Success)
        {
            var fn = m.Groups[1].Value;
            if (!ArgFunctions.TryGetValue(fn, out var arity))
                throw new SkillFormatException($"{path}: unknown function '{fn}' (use {string.Join(", ", ArgFunctions.Keys)})");
            var args = m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (args.Length != arity)
                throw new SkillFormatException($"{path}: {fn} takes {arity} argument(s)");
            foreach (var a in args)
                if (!Token().IsMatch(a))
                    throw new SkillFormatException($"{path}: bad argument '{a}' to {fn}");
            return new FuncArg(fn, args);
        }
        if (!Token().IsMatch(text))
            throw new SkillFormatException($"{path}: '{text}' is not a name, number or function call");
        return new NameArg(text);
    }

    #region Serialize

    public static string Serialize(Skill skill, bool indented = true) =>
        ToNode(skill).ToJsonString(new JsonSerializerOptions { WriteIndented = indented });

    public static string SerializeLibrary(IEnumerable<Skill> skills) =>
        new JsonArray(skills.Select(s => (JsonNode)ToNode(s)).ToArray())
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static JsonObject ToNode(Skill s)
    {
        var obj = new JsonObject { ["name"] = s.Name };
        if (s.Description.Length > 0)
            obj["description"] = s.Description;
        obj["params"] = new JsonArray(s.Params.Select(p => (JsonNode)new JsonObject
        {
            ["name"] = p.Name,
            ["type"] = ParamTypes.First(kv => kv.Value == p.Type).Key
        }).ToArray());
        obj["pre"] = new JsonArray(s.Pre.Select(c => (JsonNode)JsonValue.Create(c.Text)!).ToArray());
        obj["effect"] = new JsonArray(s.Effect.Select(c => (JsonNode)JsonValue.Create(c.Text)!).ToArray());
        obj["body"] = BlockNode(s.Body);
        return obj;
    }

    private static JsonArray BlockNode(IEnumerable<SkillStmt> block) =>
        new(block.Select(StatementNode).ToArray());

    private static JsonNode StatementNode(SkillStmt stmt) => stmt switch
    {
        CallStmt c => c.Args.Count == 0
            ? new JsonObject { ["call"] = c.Action }
            : new JsonObject { ["call"] = c.Action, ["args"] = new JsonArray(c.Args.Select(a => (JsonNode)JsonValue.Create(a.ToString())!).ToArray()) },
        IfStmt i => i.Else.Count == 0
            ? new JsonObject { ["if"] = i.Condition.Text, ["then"] = BlockNode(i.Then) }
            : new JsonObject { ["if"] = i.Condition.Text, ["then"] = BlockNode(i.Then), ["else"] = BlockNode(i.Else) },
        UntilStmt u => new JsonObject { ["until"] = u.Condition.Text, ["max"] = u.Max, ["do"] = BlockNode(u.Body) },
        RepeatStmt r => new JsonObject { ["repeat"] = r.Count, ["do"] = BlockNode(r.Body) },
        FailStmt f => new JsonObject { ["fail"] = f.Reason },
        _ => throw new ArgumentOutOfRangeException(nameof(stmt))
    };

    #endregion

    #region Helpers

    private static JsonObject Object(JsonNode? node, string path) =>
        node as JsonObject ?? throw new SkillFormatException($"{path}: expected an object");

    private static JsonArray Array(JsonNode? node, string path) =>
        node as JsonArray ?? throw new SkillFormatException($"{path}: expected a list");

    private static JsonNode Required(JsonObject obj, string key, string path) =>
        obj[key] ?? throw new SkillFormatException($"{path}: missing \"{key}\"");

    private static string String(JsonNode? node, string path) =>
        node is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : throw new SkillFormatException($"{path}: expected a string");

    private static string Identifier(JsonNode node, string path)
    {
        var s = String(node, path);
        return IdentifierPattern().IsMatch(s)
            ? s
            : throw new SkillFormatException($"{path}: '{s}' must be lowercase letters, digits and _ , starting with a letter");
    }

    private static int PositiveInt(JsonNode node, string path) =>
        node is JsonValue v && v.TryGetValue<int>(out var n) && n >= 1
            ? n
            : throw new SkillFormatException($"{path}: expected a whole number ≥ 1");

    private static void Keys(JsonObject obj, string path, string[] allowed)
    {
        var unknown = obj.Select(kv => kv.Key).FirstOrDefault(k => !allowed.Contains(k));
        if (unknown is not null)
            throw new SkillFormatException($"{path}: unknown key \"{unknown}\" (allowed: {string.Join(", ", allowed)})");
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^([a-z_]+)\((.*)\)$")]
    private static partial Regex FunctionCall();

    [GeneratedRegex(@"^-?[A-Za-z0-9_]+$")]
    private static partial Regex Token();

    #endregion
}
