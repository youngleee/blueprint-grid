using Dungeon.Core;

namespace Dungeon.Entities;

public enum GuardState { Idle, Alert, Calm, Distracted, Bribed, Hostile }

public sealed record GuardTraits(
    double CalmChance,
    double FoodChance,
    double CoinChance,
    double ThrowChance,
    int DistractMinTicks,
    int DistractMaxTicks,
    int MaxPatience,
    int DetectionRadius = 2);

// Stationary guard on a chokepoint. Blocks passage unless calmed, bribed or distracted.
public sealed class Guard
{
    public string Id { get; }
    public Cell Position { get; }
    public GuardTraits Traits { get; }
    public GuardState State { get; set; }

    // Ticks the guard tolerates an un-handled agent within detection range before turning hostile.
    public int Patience { get; set; }

    public int DistractedRemaining { get; set; }

    public bool BlocksMovement => State is GuardState.Idle or GuardState.Alert or GuardState.Hostile;

    // States in which talk/give/throw can still change the guard's mind.
    public bool IsPersuadable => State is GuardState.Idle or GuardState.Alert;

    public Guard(string id, Cell position, GuardTraits traits)
    {
        Id = id;
        Position = position;
        Traits = traits;
        State = GuardState.Idle;
        Patience = traits.MaxPatience;
    }

    public Guard Clone() => new(Id, Position, Traits)
    {
        State = State,
        Patience = Patience,
        DistractedRemaining = DistractedRemaining
    };

    public override string ToString() => Id;
}
