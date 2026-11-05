namespace Dungeon.Core;

public enum ActionStatus
{
    // The action happened and had its intended effect.
    Success,

    // The action happened (a tick passed) but did not have its intended effect.
    Failed,

    // The action was not possible; no tick passed.
    Invalid
}

public sealed record ActionResult(string Action, ActionStatus Status, string Message)
{
    public override string ToString() => $"{Action} -> {Status.ToString().ToLowerInvariant()}: {Message}";
}
