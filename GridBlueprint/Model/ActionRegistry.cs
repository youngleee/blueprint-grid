using System.Collections.Generic;

namespace GridBlueprint.Model;

// Stores the actions currently available to an agent.
public sealed class ActionRegistry
{
    private readonly Dictionary<string, AgentAction> _actions = new();

    public IReadOnlyCollection<AgentAction> Actions => _actions.Values;

    public void Register(AgentAction action)
    {
        _actions[action.Name] = action;
    }

    public bool TryGet(string name, out AgentAction action)
    {
        return _actions.TryGetValue(name, out action);
    }
}
