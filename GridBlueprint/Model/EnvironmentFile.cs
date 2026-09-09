using System;
using System.IO;

namespace GridBlueprint.Model;

// Loads local settings without overriding variables set in the terminal.
public static class EnvironmentFile
{
    public static void Load(string path)
    {
        if (!File.Exists(path))
            return;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                continue;

            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
