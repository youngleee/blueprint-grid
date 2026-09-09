using System.Collections.Generic;

namespace GridBlueprint.Model;

// Stores the actions currently available to an agent.
public sealed class ActionRegistry
{
    private readonly Dictionary<string, AgentAction> _actions = new();
    private readonly Dictionary<string, CompositeAction> _compositeActions = new();

    public IReadOnlyCollection<AgentAction> Actions => _actions.Values;
    public IReadOnlyCollection<CompositeAction> CompositeActions => _compositeActions.Values;

    public void Register(AgentAction action)
    {
        _actions[action.Name] = action;
    }

    public void Register(CompositeAction action)
    {
        _compositeActions[action.Name] = action;
    }

    public bool TryGet(string name, out AgentAction action)
    {
        return _actions.TryGetValue(name, out action);
    }

    public bool TryGetComposite(string name, out CompositeAction action)
    {
        return _compositeActions.TryGetValue(name, out action);
    }

    public bool Contains(string name)
    {
        return _actions.ContainsKey(name) || _compositeActions.ContainsKey(name);
    }
}
