using Dungeon.Entities;

namespace Dungeon.Core;

public enum EpisodeStatus { Running, Succeeded, Failed }

// Mutable episode state. Turn-based: each valid action costs exactly one tick; entities
// react after the agent's action within the same tick. Invalid actions cost no tick.
public sealed class DungeonWorld
{
    public const int DefaultSightRadius = 3;
    public const int ThrowRange = 3;
    public const int ThrowDistractRadius = 4;
    public const int TalkRange = 2;

    private readonly bool[,] _known;
    private readonly List<Door> _doors;
    private readonly List<Guard> _guards;
    private readonly List<Item> _items;

    public DungeonMap Map { get; }
    public int Seed { get; }
    public int SightRadius { get; }
    public Cell AgentPosition { get; private set; }
    public long Tick { get; private set; }
    public Rng OutcomeRng { get; }
    public IReadOnlySet<Cell> Visible { get; private set; }
    public EpisodeStatus Status { get; private set; }
    public string? FailureReason { get; private set; }

    public IReadOnlyList<Door> Doors => _doors;
    public IReadOnlyList<Guard> Guards => _guards;
    public IReadOnlyList<Item> Items => _items;
    public IEnumerable<Item> Inventory => _items.Where(i => i.IsHeld);

    public bool AtExit => AgentPosition == Map.Exit;

    public static DungeonWorld Create(int seed, GeneratorOptions? options = null, int sightRadius = DefaultSightRadius) =>
        new(Scenario.Empty(Generator.Generate(seed, options)), seed, sightRadius);

    public DungeonWorld(DungeonMap map, int seed, int sightRadius = DefaultSightRadius)
        : this(Scenario.Empty(map), seed, sightRadius)
    {
    }

    public DungeonWorld(Scenario scenario, int seed, int sightRadius = DefaultSightRadius)
    {
        Map = scenario.Map;
        Seed = seed;
        SightRadius = sightRadius;
        AgentPosition = Map.Start;
        OutcomeRng = Rng.ForStream(seed, "outcome");
        _known = new bool[Map.Width, Map.Height];
        _doors = scenario.Doors.Select(d => d.Clone()).ToList();
        _guards = scenario.Guards.Select(g => g.Clone()).ToList();
        _items = scenario.Items.Select(i => i.Clone()).ToList();
        Visible = new HashSet<Cell>();
        UpdateVisibility();
    }

    private DungeonWorld(DungeonWorld other)
    {
        Map = other.Map;
        Seed = other.Seed;
        SightRadius = other.SightRadius;
        AgentPosition = other.AgentPosition;
        Tick = other.Tick;
        OutcomeRng = other.OutcomeRng.Clone();
        Status = other.Status;
        FailureReason = other.FailureReason;
        _known = (bool[,])other._known.Clone();
        _doors = other._doors.Select(d => d.Clone()).ToList();
        _guards = other._guards.Select(g => g.Clone()).ToList();
        _items = other._items.Select(i => i.Clone()).ToList();
        Visible = new HashSet<Cell>(other.Visible);
    }

    // Deep copy of all mutable state; the map is immutable and shared.
    public DungeonWorld Clone() => new(this);

    #region Queries

    public bool IsKnown(Cell c) => Map.InBounds(c) && _known[c.X, c.Y];

    public IEnumerable<Cell> KnownCells()
    {
        for (var x = 0; x < Map.Width; x++)
            for (var y = 0; y < Map.Height; y++)
                if (_known[x, y])
                    yield return new Cell(x, y);
    }

    public Door? DoorAt(Cell c) => _doors.FirstOrDefault(d => d.Position == c);

    public Guard? GuardAt(Cell c) => _guards.FirstOrDefault(g => g.Position == c);

    public bool IsOpaque(Cell c) => !Map.IsFloor(c) || DoorAt(c) is { IsOpen: false };

    public bool IsPassable(Cell c) =>
        Map.IsFloor(c) && DoorAt(c) is not { IsOpen: false } && GuardAt(c) is not { BlocksMovement: true };

    public bool IsVisible(Cell c) => Visible.Contains(c);

    public Item? HeldItem(string id) => _items.FirstOrDefault(i => i.Id == id && i.IsHeld);

    #endregion

    #region Primitives

    public ActionResult Move(Direction direction)
    {
        var name = $"move({direction})";
        if (Status != EpisodeStatus.Running)
            return Invalid(name, "episode is over");

        var target = AgentPosition.Step(direction);
        ActionResult result;
        if (IsPassable(target))
        {
            AgentPosition = target;
            result = new ActionResult(name, ActionStatus.Success, $"moved to {target}");
        }
        else
        {
            var reason = DoorAt(target) is { IsOpen: false } door ? $"{door} is closed"
                : GuardAt(target) is { } guard ? $"{guard} blocks the way"
                : "wall";
            result = new ActionResult(name, ActionStatus.Failed, reason);
        }
        EndTick();
        return result;
    }

    public ActionResult Wait()
    {
        if (Status != EpisodeStatus.Running)
            return Invalid("wait", "episode is over");
        EndTick();
        return new ActionResult("wait", ActionStatus.Success, "waited");
    }

    // Spends a tick observing. Traps (planned) can only be revealed this way.
    public ActionResult Look()
    {
        if (Status != EpisodeStatus.Running)
            return Invalid("look", "episode is over");
        EndTick();
        return new ActionResult("look", ActionStatus.Success, "looked around");
    }

    public ActionResult PickUp()
    {
        if (Status != EpisodeStatus.Running)
            return Invalid("pick_up", "episode is over");
        var item = _items.FirstOrDefault(i => i.Position == AgentPosition);
        if (item is null)
            return Invalid("pick_up", "nothing here");
        item.Position = null;
        EndTick();
        return new ActionResult($"pick_up", ActionStatus.Success, $"picked up {item}");
    }

    public ActionResult Talk(string guardId)
    {
        var name = $"talk({guardId})";
        if (!TryGetInteractableGuard(name, guardId, TalkRange, out var guard, out var invalid))
            return invalid;

        var calmed = guard.IsPersuadable && OutcomeRng.Chance(guard.Traits.CalmChance);
        if (calmed)
            guard.State = GuardState.Calm;
        EndTick();
        return calmed
            ? new ActionResult(name, ActionStatus.Success, $"{guard} calmed down")
            : new ActionResult(name, ActionStatus.Failed, $"{guard} is not convinced");
    }

    public ActionResult Give(string itemId, string guardId)
    {
        var name = $"give({itemId},{guardId})";
        if (!TryGetInteractableGuard(name, guardId, 1, out var guard, out var invalid))
            return invalid;
        var item = HeldItem(itemId);
        if (item is null)
            return Invalid(name, $"not holding {itemId}");

        var (accepted, newState, chance) = item.Type switch
        {
            ItemType.Food => (true, GuardState.Distracted, guard.Traits.FoodChance),
            ItemType.Coin => (true, GuardState.Bribed, guard.Traits.CoinChance),
            _ => (false, guard.State, 0.0)
        };

        var success = accepted && guard.IsPersuadable && OutcomeRng.Chance(chance);
        if (success)
        {
            _items.Remove(item);
            guard.State = newState;
            if (newState == GuardState.Distracted)
                guard.DistractedRemaining = RollDistraction(guard);
        }
        EndTick();
        return success
            ? new ActionResult(name, ActionStatus.Success, $"{guard} took {item} and is now {newState.ToString().ToLowerInvariant()}")
            : new ActionResult(name, ActionStatus.Failed, $"{guard} refused {item}");
    }

    public ActionResult Throw(string itemId, Cell target)
    {
        var name = $"throw({itemId},{target})";
        if (Status != EpisodeStatus.Running)
            return Invalid(name, "episode is over");
        var item = HeldItem(itemId);
        if (item is null)
            return Invalid(name, $"not holding {itemId}");
        if (!Map.IsFloor(target) || IsOpaque(target) || AgentPosition.Chebyshev(target) > ThrowRange
            || !Visibility.HasLineOfSight(AgentPosition, target, IsOpaque))
            return Invalid(name, $"cannot throw to {target}");

        item.Position = target;
        var distracted = new List<Guard>();
        foreach (var guard in _guards)
        {
            if (!guard.IsPersuadable || guard.Position.Chebyshev(target) > ThrowDistractRadius)
                continue;
            if (!OutcomeRng.Chance(guard.Traits.ThrowChance))
                continue;
            guard.State = GuardState.Distracted;
            guard.DistractedRemaining = RollDistraction(guard);
            distracted.Add(guard);
        }
        EndTick();
        return distracted.Count > 0
            ? new ActionResult(name, ActionStatus.Success, $"distracted {string.Join(", ", distracted)}")
            : new ActionResult(name, ActionStatus.Failed, "nobody reacted");
    }

    // Use an item (or bare hands when itemId is null) on an adjacent door.
    public ActionResult Use(string? itemId, string targetId)
    {
        var name = $"use({itemId ?? "none"},{targetId})";
        if (Status != EpisodeStatus.Running)
            return Invalid(name, "episode is over");
        var door = _doors.FirstOrDefault(d => d.Id == targetId);
        if (door is null)
            return Invalid(name, $"no door {targetId}");
        if (door.Position.Manhattan(AgentPosition) != 1)
            return Invalid(name, $"{door} is not adjacent");
        if (door.IsOpen)
            return Invalid(name, $"{door} is already open");
        Item? item = null;
        if (itemId is not null && (item = HeldItem(itemId)) is null)
            return Invalid(name, $"not holding {itemId}");

        var (opened, message) = (door.Kind, item?.Type) switch
        {
            (DoorKind.Locked, ItemType.Key) when item!.Opens == door.Id => (true, "unlocked"),
            (DoorKind.Locked, ItemType.Key) => (false, "key does not fit"),
            (DoorKind.Locked, ItemType.Lockpick) => OutcomeRng.Chance(door.PickChance)
                ? (true, "lock picked")
                : (false, "lockpick slipped"),
            (DoorKind.Locked, _) => (false, "door is locked"),
            (DoorKind.Jammed, null) => OutcomeRng.Chance(door.PushChance)
                ? (true, "pushed open")
                : (false, "door is stuck"),
            (DoorKind.Jammed, _) => (false, "item does not help, door is stuck"),
            _ => (false, "nothing happened")
        };

        if (opened)
            door.IsOpen = true;
        EndTick();
        return new ActionResult(name, opened ? ActionStatus.Success : ActionStatus.Failed, $"{door}: {message}");
    }

    #endregion

    private bool TryGetInteractableGuard(string name, string guardId, int range, out Guard guard, out ActionResult invalid)
    {
        guard = _guards.FirstOrDefault(g => g.Id == guardId)!;
        invalid = null!;
        if (Status != EpisodeStatus.Running)
            invalid = Invalid(name, "episode is over");
        else if (guard is null)
            invalid = Invalid(name, $"no guard {guardId}");
        else if (guard.Position.Chebyshev(AgentPosition) > range)
            invalid = Invalid(name, $"{guard} is out of range {range}");
        else if (!Visibility.HasLineOfSight(AgentPosition, guard.Position, IsOpaque))
            invalid = Invalid(name, $"{guard} is not in sight");
        return invalid is null;
    }

    private int RollDistraction(Guard guard) =>
        OutcomeRng.Next(guard.Traits.DistractMinTicks, guard.Traits.DistractMaxTicks + 1);

    private static ActionResult Invalid(string name, string message) => new(name, ActionStatus.Invalid, message);

    private void EndTick()
    {
        Tick++;
        UpdateVisibility();

        if (AtExit)
        {
            Status = EpisodeStatus.Succeeded;
            return;
        }

        foreach (var guard in _guards)
        {
            UpdateGuard(guard);
            if (guard.State == GuardState.Hostile)
            {
                Status = EpisodeStatus.Failed;
                FailureReason = $"caught by {guard}";
                return;
            }
        }
    }

    private void UpdateGuard(Guard guard)
    {
        var inRange = guard.Position.Chebyshev(AgentPosition) <= guard.Traits.DetectionRadius
            && Visibility.HasLineOfSight(guard.Position, AgentPosition, IsOpaque);

        switch (guard.State)
        {
            case GuardState.Distracted:
                if (--guard.DistractedRemaining <= 0)
                {
                    guard.State = inRange ? GuardState.Alert : GuardState.Idle;
                    guard.Patience = guard.Traits.MaxPatience;
                }
                break;
            case GuardState.Idle when inRange:
                guard.State = GuardState.Alert;
                guard.Patience = guard.Traits.MaxPatience;
                break;
            case GuardState.Alert when !inRange:
                guard.State = GuardState.Idle;
                guard.Patience = guard.Traits.MaxPatience;
                break;
            case GuardState.Alert:
                if (--guard.Patience <= 0)
                    guard.State = GuardState.Hostile;
                break;
        }
    }

    private void UpdateVisibility()
    {
        Visible = Visibility.VisibleFrom(Map, AgentPosition, SightRadius, IsOpaque);
        foreach (var c in Visible)
            _known[c.X, c.Y] = true;
    }
}
