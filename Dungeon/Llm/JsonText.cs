namespace Dungeon.Llm;

public static class JsonText
{
    // Models sometimes wrap JSON in ``` fences despite JSON mode; accept that.
    public static string StripFences(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```"))
            return s;
        var start = s.IndexOf('\n');
        var end = s.LastIndexOf("```", StringComparison.Ordinal);
        return start >= 0 && end > start ? s[(start + 1)..end].Trim() : s.Trim('`');
    }
}
