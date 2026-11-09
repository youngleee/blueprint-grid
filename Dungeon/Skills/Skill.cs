using Dungeon.Agent;

namespace Dungeon.Skills;

public sealed record SkillParam(string Name, ParamType Type);

// A condition plus the text it was written as; the text is what the LLM and the logs see.
public sealed record SkillCondition(string Text, Condition Parsed)
{
    public bool Holds(FactSet facts, IReadOnlyDictionary<string, string> bindings) => Parsed.Evaluate(facts, bindings);
    public override string ToString() => Text;
}

public abstract record SkillStmt;

public sealed record CallStmt(string Action, IReadOnlyList<ArgExpr> Args) : SkillStmt;

public sealed record IfStmt(SkillCondition Condition, IReadOnlyList<SkillStmt> Then, IReadOnlyList<SkillStmt> Else) : SkillStmt;

// Loop that checks its condition before every pass and stops when it holds, or after Max passes.
public sealed record UntilStmt(SkillCondition Condition, int Max, IReadOnlyList<SkillStmt> Body) : SkillStmt;

public sealed record RepeatStmt(int Count, IReadOnlyList<SkillStmt> Body) : SkillStmt;

public sealed record FailStmt(string Reason) : SkillStmt;

// Call argument: a parameter name or literal, or a function of names resolved at run time.
public abstract record ArgExpr;

public sealed record NameArg(string Name) : ArgExpr
{
    public override string ToString() => Name;
}

public sealed record FuncArg(string Function, IReadOnlyList<string> Args) : ArgExpr
{
    public override string ToString() => $"{Function}({string.Join(", ", Args)})";

    public bool Equals(FuncArg? other) =>
        other is not null && Function == other.Function && Args.SequenceEqual(other.Args);

    public override int GetHashCode() => HashCode.Combine(Function, string.Join(",", Args));
}

// A reusable, parameterized, closed-loop action (PLAN §3, §7).
public sealed record Skill(
    string Name,
    string Description,
    IReadOnlyList<SkillParam> Params,
    IReadOnlyList<SkillCondition> Pre,
    IReadOnlyList<SkillCondition> Effect,
    IReadOnlyList<SkillStmt> Body);
