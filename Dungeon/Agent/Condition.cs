using System.Globalization;

namespace Dungeon.Agent;

// Condition language for skill preconditions/effects and DSL branches (PLAN §7.1).
//
//   cond   := or
//   or     := and ('or' and)*
//   and    := unary ('and' unary)*
//   unary  := 'not' unary | '(' cond ')' | 'true' | 'false' | call [compare]
//   call   := name '(' [term (',' term)*] ')'      also and(...), or(...), not(...)
//   compare:= ('=='|'!='|'<'|'<='|'>'|'>=') term | 'in' '[' term (',' term)* ']'
//   term   := identifier | integer
//
// Identifiers are resolved through parameter bindings first, then taken literally.
// Unknown entities make a condition false rather than throwing: the agent simply lacks the fact.
public abstract record Condition
{
    public abstract bool Evaluate(FactSet facts, IReadOnlyDictionary<string, string>? bindings = null);

    // All identifier terms used, for the skill validator (params must be declared).
    public abstract IEnumerable<string> Identifiers();

    public static Condition Parse(string text) => new ConditionParser(text).ParseAll();

    public static bool TryParse(string text, out Condition? condition, out string error)
    {
        try
        {
            condition = Parse(text);
            error = "";
            return true;
        }
        catch (FormatException e)
        {
            condition = null;
            error = e.Message;
            return false;
        }
    }
}

public sealed record Const(bool Value) : Condition
{
    public override bool Evaluate(FactSet facts, IReadOnlyDictionary<string, string>? bindings = null) => Value;
    public override IEnumerable<string> Identifiers() => [];
    public override string ToString() => Value ? "true" : "false";
}

public sealed record Not(Condition Inner) : Condition
{
    public override bool Evaluate(FactSet facts, IReadOnlyDictionary<string, string>? bindings = null) =>
        !Inner.Evaluate(facts, bindings);
    public override IEnumerable<string> Identifiers() => Inner.Identifiers();
    public override string ToString() => $"not({Inner})";
}

public sealed record And(IReadOnlyList<Condition> Parts) : Condition
{
    public override bool Evaluate(FactSet facts, IReadOnlyDictionary<string, string>? bindings = null) =>
        Parts.All(p => p.Evaluate(facts, bindings));
    public override IEnumerable<string> Identifiers() => Parts.SelectMany(p => p.Identifiers());
    public override string ToString() => $"and({string.Join(", ", Parts)})";
}

public sealed record Or(IReadOnlyList<Condition> Parts) : Condition
{
    public override bool Evaluate(FactSet facts, IReadOnlyDictionary<string, string>? bindings = null) =>
        Parts.Any(p => p.Evaluate(facts, bindings));
    public override IEnumerable<string> Identifiers() => Parts.SelectMany(p => p.Identifiers());
    public override string ToString() => $"or({string.Join(", ", Parts)})";
}

public enum CompareOp { Eq, Ne, Lt, Le, Gt, Ge, In }

public sealed record Predicate(string Name, IReadOnlyList<string> Args, CompareOp? Op, IReadOnlyList<string> Values) : Condition
{
    public override bool Evaluate(FactSet facts, IReadOnlyDictionary<string, string>? bindings = null)
    {
        var args = Args.Select(a => Resolve(a, bindings)).ToList();
        var values = Values.Select(v => Resolve(v, bindings)).ToList();
        var value = Predicates.Evaluate(Name, args, facts);
        if (Op is null)
            return value is bool b && b;
        if (value is null)
            return false;
        return Compare(value, Op.Value, values);
    }

    public override IEnumerable<string> Identifiers() =>
        Args.Concat(Values).Where(t => !int.TryParse(t, out _));

    public override string ToString()
    {
        var call = $"{Name}({string.Join(", ", Args)})";
        return Op switch
        {
            null => call,
            CompareOp.In => $"{call} in [{string.Join(", ", Values)}]",
            _ => $"{call} {OpText(Op.Value)} {Values[0]}"
        };
    }

    private static string Resolve(string term, IReadOnlyDictionary<string, string>? bindings) =>
        bindings is not null && bindings.TryGetValue(term, out var bound) ? bound : term;

    private static bool Compare(object value, CompareOp op, IReadOnlyList<string> values)
    {
        if (op == CompareOp.In)
            return values.Any(v => Equal(value, v));
        if (op is CompareOp.Eq)
            return Equal(value, values[0]);
        if (op is CompareOp.Ne)
            return !Equal(value, values[0]);
        if (value is not int left || !int.TryParse(values[0], out var right))
            return false;
        return op switch
        {
            CompareOp.Lt => left < right,
            CompareOp.Le => left <= right,
            CompareOp.Gt => left > right,
            CompareOp.Ge => left >= right,
            _ => false
        };
    }

    private static bool Equal(object value, string literal) => value switch
    {
        int i => int.TryParse(literal, out var n) && i == n,
        bool b => bool.TryParse(literal, out var lb) && b == lb,
        string s => string.Equals(s, literal, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    public static string OpText(CompareOp op) => op switch
    {
        CompareOp.Eq => "==", CompareOp.Ne => "!=", CompareOp.Lt => "<",
        CompareOp.Le => "<=", CompareOp.Gt => ">", CompareOp.Ge => ">=", _ => "in"
    };
}

public enum ValueType { Bool, Int, Text }

public static class Predicates
{
    // name → (argument count, return type). The validator uses this to reject unknown predicates.
    public static readonly IReadOnlyDictionary<string, (int Arity, ValueType Type)> Signatures =
        new Dictionary<string, (int, ValueType)>
        {
            ["visible"] = (1, ValueType.Bool),   // entity or exit in sight right now
            ["known"] = (1, ValueType.Bool),     // entity or exit seen at some point
            ["adjacent"] = (1, ValueType.Bool),  // orthogonally adjacent
            ["has"] = (1, ValueType.Bool),       // holding item id, or any item of that type
            ["at_exit"] = (0, ValueType.Bool),
            ["state"] = (1, ValueType.Text),     // last-known state
            ["type"] = (1, ValueType.Text),      // guard | locked | jammed | key | food | ...
            ["dist"] = (1, ValueType.Int),       // Chebyshev distance to last-known position
            ["count"] = (1, ValueType.Int),      // number of held items of a type
            ["has_key"] = (1, ValueType.Bool),   // holding the key that opens door x
        };

    public static object? Evaluate(string name, IReadOnlyList<string> args, FactSet f)
    {
        EntityFact? Entity(string id) => f.Entities.FirstOrDefault(e => e.Sighting.Id == id);
        EntitySighting? Held(string id) => f.Inventory.FirstOrDefault(s => s.Id == id);
        var a = args.Count > 0 ? args[0] : "";

        return name switch
        {
            "at_exit" => f.Exit is { } x && f.Position == x,
            "visible" when a == "exit" => f.ExitInSight,
            "visible" => Entity(a)?.InSight ?? false,
            "known" when a == "exit" => f.Exit is not null,
            "known" => Entity(a) is not null || Held(a) is not null,
            "adjacent" when a == "exit" => f.Exit is { } x && f.Position.Manhattan(x) == 1,
            "adjacent" => Entity(a)?.Adjacent ?? false,
            "has" => Held(a) is not null || f.Inventory.Any(s => s.Subtype == a),
            "state" => (object?)Entity(a)?.Sighting.State ?? Held(a)?.State,
            "type" => (object?)Entity(a)?.Sighting.Subtype ?? Held(a)?.Subtype,
            "dist" when a == "exit" => f.Exit is { } x ? f.Position.Chebyshev(x) : null,
            "dist" => Entity(a)?.Distance,
            "count" => f.Inventory.Count(s => s.Subtype == a || s.Id == a),
            "has_key" => f.Inventory.Any(s => s.Opens == a),
            _ => throw new FormatException($"unknown predicate '{name}'")
        };
    }
}

internal sealed class ConditionParser
{
    private readonly List<string> _tokens;
    private int _pos;

    public ConditionParser(string text) => _tokens = Tokenize(text);

    public Condition ParseAll()
    {
        if (_tokens.Count == 0)
            throw new FormatException("empty condition");
        var c = ParseOr();
        if (_pos != _tokens.Count)
            throw new FormatException($"unexpected '{_tokens[_pos]}'");
        return c;
    }

    private Condition ParseOr()
    {
        var parts = new List<Condition> { ParseAnd() };
        while (Accept("or"))
            parts.Add(ParseAnd());
        return parts.Count == 1 ? parts[0] : new Or(parts);
    }

    private Condition ParseAnd()
    {
        var parts = new List<Condition> { ParseUnary() };
        while (Accept("and"))
            parts.Add(ParseUnary());
        return parts.Count == 1 ? parts[0] : new And(parts);
    }

    private Condition ParseUnary()
    {
        if (Peek() is "not" or "and" or "or" && PeekAt(1) == "(")
        {
            var op = Next();
            Expect("(");
            var parts = new List<Condition> { ParseOr() };
            while (Accept(","))
                parts.Add(ParseOr());
            Expect(")");
            return op switch
            {
                "not" when parts.Count == 1 => new Not(parts[0]),
                "not" => throw new FormatException("not(...) takes exactly one condition"),
                "and" => new And(parts),
                _ => new Or(parts)
            };
        }
        if (Accept("not"))
            return new Not(ParseUnary());
        if (Accept("("))
        {
            var inner = ParseOr();
            Expect(")");
            return inner;
        }
        if (Accept("true"))
            return new Const(true);
        if (Accept("false"))
            return new Const(false);
        return ParsePredicate();
    }

    private Condition ParsePredicate()
    {
        var name = Next();
        if (!IsIdentifier(name))
            throw new FormatException($"expected predicate, got '{name}'");
        if (!Predicates.Signatures.TryGetValue(name, out var sig))
            throw new FormatException($"unknown predicate '{name}'");

        Expect("(");
        var args = new List<string>();
        if (Peek() != ")")
        {
            args.Add(Term());
            while (Accept(","))
                args.Add(Term());
        }
        Expect(")");
        if (args.Count != sig.Arity)
            throw new FormatException($"{name} takes {sig.Arity} argument(s), got {args.Count}");

        CompareOp? op = Peek() switch
        {
            "==" => CompareOp.Eq, "!=" => CompareOp.Ne, "<" => CompareOp.Lt,
            "<=" => CompareOp.Le, ">" => CompareOp.Gt, ">=" => CompareOp.Ge, "in" => CompareOp.In,
            _ => null
        };
        var values = new List<string>();
        if (op is not null)
        {
            Next();
            if (sig.Type == ValueType.Bool)
                throw new FormatException($"{name}(...) is a yes/no fact and cannot be compared");
            if (op is CompareOp.Lt or CompareOp.Le or CompareOp.Gt or CompareOp.Ge && sig.Type != ValueType.Int)
                throw new FormatException($"{name}(...) is not a number; use == / != / in");
            if (op == CompareOp.In)
            {
                Expect("[");
                values.Add(Term());
                while (Accept(","))
                    values.Add(Term());
                Expect("]");
            }
            else
            {
                values.Add(Term());
            }
        }
        else if (sig.Type != ValueType.Bool)
        {
            throw new FormatException($"{name}(...) needs a comparison, e.g. {name}(x) == value");
        }
        return new Predicate(name, args, op, values);
    }

    private string Term()
    {
        var t = Next();
        if (!IsIdentifier(t) && !int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            throw new FormatException($"expected name or number, got '{t}'");
        return t;
    }

    private static bool IsIdentifier(string t) =>
        t.Length > 0 && (char.IsLetter(t[0]) || t[0] == '_') && t.All(c => char.IsLetterOrDigit(c) || c == '_');

    private string? Peek() => PeekAt(0);
    private string? PeekAt(int offset) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : null;

    private string Next() => _pos < _tokens.Count ? _tokens[_pos++] : throw new FormatException("unexpected end of condition");

    private bool Accept(string token)
    {
        if (Peek() != token)
            return false;
        _pos++;
        return true;
    }

    private void Expect(string token)
    {
        if (!Accept(token))
            throw new FormatException($"expected '{token}'{(Peek() is { } p ? $", got '{p}'" : " at end")}");
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '(' or ')' or ',' or '[' or ']') { tokens.Add(c.ToString()); i++; continue; }
            if (c is '=' or '!' or '<' or '>')
            {
                var two = i + 1 < text.Length ? text.Substring(i, 2) : "";
                if (two is "==" or "!=" or "<=" or ">=") { tokens.Add(two); i += 2; continue; }
                if (c is '<' or '>') { tokens.Add(c.ToString()); i++; continue; }
                throw new FormatException($"unexpected '{c}' at {i}");
            }
            var start = i;
            if (c == '-' || char.IsLetterOrDigit(c) || c == '_')
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(text[start..i]);
                continue;
            }
            throw new FormatException($"unexpected '{c}' at {i}");
        }
        return tokens;
    }
}
