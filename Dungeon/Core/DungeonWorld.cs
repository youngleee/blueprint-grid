namespace Dungeon.Core;

public enum StepOutcome { Moved, Blocked, Waited }

// Mutable episode state. Turn-based: every primitive is exactly one tick; entities
// (added in week 3) react after the agent's action within the same tick.
public sealed class DungeonWorld
{
    public const int DefaultSightRadius = 3;

    private readonly bool[,] _known;

    public DungeonMap Map { get; }
    public int Seed { get; }
    public int SightRadius { get; }
    public Cell AgentPosition { get; private set; }
    public long Tick { get; private set; }
    public Rng EntityRng { get; }
    public Rng OutcomeRng { get; }
    public IReadOnlySet<Cell> Visible { get; private set; }

    public bool AtExit => AgentPosition == Map.Exit;

    public static DungeonWorld Create(int seed, GeneratorOptions? options = null, int sightRadius = DefaultSightRadius) =>
        new(Generator.Generate(seed, options), seed, sightRadius);

    public DungeonWorld(DungeonMap map, int seed, int sightRadius = DefaultSightRadius)
        : this(map, seed, sightRadius, map.Start, 0,
            Rng.ForStream(seed, "entity"), Rng.ForStream(seed, "outcome"),
            new bool[map.Width, map.Height])
    {
        UpdateVisibility();
    }

    private DungeonWorld(DungeonMap map, int seed, int sightRadius, Cell agent, long tick,
        Rng entityRng, Rng outcomeRng, bool[,] known)
    {
        Map = map;
        Seed = seed;
        SightRadius = sightRadius;
        AgentPosition = agent;
        Tick = tick;
        EntityRng = entityRng;
        OutcomeRng = outcomeRng;
        _known = known;
        Visible = new HashSet<Cell>();
    }

    // Deep copy of all mutable state; the map is immutable and shared.
    public DungeonWorld Clone()
    {
        var copy = new DungeonWorld(Map, Seed, SightRadius, AgentPosition, Tick,
            EntityRng.Clone(), OutcomeRng.Clone(), (bool[,])_known.Clone());
        copy.Visible = new HashSet<Cell>(Visible);
        return copy;
    }

    public bool IsKnown(Cell c) => Map.InBounds(c) && _known[c.X, c.Y];

    public IEnumerable<Cell> KnownCells()
    {
        for (var x = 0; x < Map.Width; x++)
            for (var y = 0; y < Map.Height; y++)
                if (_known[x, y])
                    yield return new Cell(x, y);
    }

    public StepOutcome Move(Direction direction)
    {
        var target = AgentPosition.Step(direction);
        var outcome = Map.IsFloor(target) ? StepOutcome.Moved : StepOutcome.Blocked;
        if (outcome == StepOutcome.Moved)
            AgentPosition = target;
        EndTick();
        return outcome;
    }

    public StepOutcome Wait()
    {
        EndTick();
        return StepOutcome.Waited;
    }

    private void EndTick()
    {
        Tick++;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        Visible = Visibility.VisibleFrom(Map, AgentPosition, SightRadius);
        foreach (var c in Visible)
            _known[c.X, c.Y] = true;
    }
}
