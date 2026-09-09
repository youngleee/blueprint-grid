using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GridBlueprint.Model;

// Persists admitted composite skills between simulation runs.
public static class SkillArchive
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Load(ActionRegistry registry, string path)
    {
        if (!File.Exists(path))
            return;

        var skills = JsonSerializer.Deserialize<List<CompositeAction>>(File.ReadAllText(path));
        if (skills == null)
            return;

        foreach (var skill in skills)
            if (ActionValidator.TryValidate(skill, registry, out var error))
                registry.Register(skill);
            else
                System.Console.WriteLine($"Skipped archived skill: {error}");
    }

    public static void Save(ActionRegistry registry, string path)
    {
        var json = JsonSerializer.Serialize(registry.CompositeActions, Options);
        File.WriteAllText(path, json);
    }
}
