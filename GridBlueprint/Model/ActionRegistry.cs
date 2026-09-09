using System;
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

    public bool TryGetApplicableComposite(int remainingX, int remainingY, out CompositeAction action)
    {
        foreach (var candidate in _compositeActions.Values)
        {
            var deltaX = 0;
            var deltaY = 0;
            foreach (var stepName in candidate.Steps)
            {
                if (!TryGet(stepName, out var step))
                    continue;

                deltaX += step.DeltaX;
                deltaY += step.DeltaY;
            }

            if (MakesProgress(deltaX, remainingX) && MakesProgress(deltaY, remainingY))
            {
                action = candidate;
                return true;
            }
        }

        action = null;
        return false;
    }

    public bool Contains(string name)
    {
        return _actions.ContainsKey(name) || _compositeActions.ContainsKey(name);
    }

    public bool ContainsCompositeSteps(IReadOnlyList<string> steps)
    {
        foreach (var skill in _compositeActions.Values)
            if (HasSameSteps(skill.Steps, steps))
                return true;

        return false;
    }

    private static bool MakesProgress(int skillDelta, int remainingDelta)
    {
        return skillDelta != 0 && remainingDelta != 0
            && Math.Sign(skillDelta) == Math.Sign(remainingDelta)
            && Math.Abs(skillDelta) <= Math.Abs(remainingDelta);
    }

    private static bool HasSameSteps(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
            if (left[index] != right[index])
                return false;

        return true;
    }
}
