using Dungeon.Core;

namespace Dungeon.Entities;

public enum DoorKind { Locked, Jammed }

public sealed class Door
{
    public string Id { get; }
    public Cell Position { get; }
    public DoorKind Kind { get; }

    // Per-attempt success chance with a lockpick (locked doors).
    public double PickChance { get; }

    // Per-attempt success chance when pushing without an item (jammed doors).
    public double PushChance { get; }

    public bool IsOpen { get; set; }

    public Door(string id, Cell position, DoorKind kind, double pickChance, double pushChance, bool isOpen = false)
    {
        Id = id;
        Position = position;
        Kind = kind;
        PickChance = pickChance;
        PushChance = pushChance;
        IsOpen = isOpen;
    }

    public Door Clone() => new(Id, Position, Kind, PickChance, PushChance, IsOpen);

    public override string ToString() => Id;
}
