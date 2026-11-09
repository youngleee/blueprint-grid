using System.Reflection;

namespace Dungeon.Skills;

public static class HandwrittenSkills
{
    private const string ResourceName = "Dungeon.Skills.Library.handwritten.json";

    // The fixed library for condition D (PLAN §9.1): written by hand, never changed by the agent.
    public static IReadOnlyList<Skill> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"embedded resource {ResourceName} missing");
        using var reader = new StreamReader(stream);
        return SkillJson.ParseLibrary(reader.ReadToEnd());
    }

    public static string Json()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
