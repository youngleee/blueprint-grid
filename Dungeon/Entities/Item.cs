namespace Dungeon.Entities;

public enum ItemType { Key, Lockpick, Food, Coin, Tool }

public sealed class Item
{
    public string Id { get; }
    public ItemType Type { get; }

    // For keys: the id of the door this key opens.
    public string? Opens { get; }

    // Null while the agent carries the item.
    public Core.Cell? Position { get; set; }

    public bool IsHeld => Position is null;

    public Item(string id, ItemType type, Core.Cell? position, string? opens = null)
    {
        Id = id;
        Type = type;
        Position = position;
        Opens = opens;
    }

    public Item Clone() => new(Id, Type, Position, Opens);

    public override string ToString() => Id;
}
