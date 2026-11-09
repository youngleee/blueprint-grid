using Dungeon.Core;

namespace Dungeon.Agent;

// The agent's handle on an episode: the world plus the agent's memory.
// All agent-side code (CLI, helpers, skills, LLM loop) acts through this class so that
// memory is updated after every tick and nothing reads hidden world state by accident.
public sealed class AgentContext
{
    public DungeonWorld World { get; }
    public AgentMemory Memory { get; }

    // Entity ids first seen during the most recent action.
    public IReadOnlyList<string> NewlySeen { get; private set; } = [];

    public AgentContext(DungeonWorld world) : this(world, new AgentMemory())
    {
        NewlySeen = Memory.Update(world);
    }

    private AgentContext(DungeonWorld world, AgentMemory memory)
    {
        World = world;
        Memory = memory;
    }

    public AgentContext Clone() => new(World.Clone(), Memory.Clone());

    public Cell Position => World.AgentPosition;
    public long Tick => World.Tick;
    public bool IsOver => World.Status != EpisodeStatus.Running;

    // Number of skills currently executing (nested calls). Bounded by the interpreter.
    public int SkillDepth { get; internal set; }

    // One primitive, used by helpers and (later) skills. Does not count as a decision.
    public ActionResult Do(Func<DungeonWorld, ActionResult> action)
    {
        var result = action(World);
        NewlySeen = Memory.Update(World);
        return result;
    }

    // Records the outcome of a whole decision so the next FactSet reports it.
    public T Decide<T>(Func<AgentContext, T> decision) where T : notnull
    {
        var outcome = decision(this);
        Memory.LastOutcome = outcome.ToString();
        return outcome;
    }

    // Resolves an entity id or the special id "exit" to the agent's last-known position.
    public Cell? KnownPosition(string id) =>
        id == "exit" ? Memory.ExitPosition : Memory.Get(id)?.Position;
}
