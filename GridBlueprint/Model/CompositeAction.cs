using System.Collections.Generic;

namespace GridBlueprint.Model;

// Describes a reusable skill made from registered primitive actions.
public sealed record CompositeAction(string Name, IReadOnlyList<string> Steps)
{
    public const int MaxSteps = 8;
}
