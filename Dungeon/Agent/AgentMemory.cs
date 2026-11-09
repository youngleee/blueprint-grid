using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Agent;

public enum EntityKind { Guard, Door, Item }

// What the agent believes about one entity. Built only from what it has seen.
public sealed record EntitySighting(
    string Id,
    EntityKind Kind,
    string Subtype,
    Cell? Position,
    string State,
    long LastSeenTick,
    string? Opens = null);

// The agent's own memory: last-known state of every entity it has seen.
// Lives outside DungeonWorld so the agent can only ever know what passed through its field of view.
public sealed class AgentMemory
{
    private readonly Dictionary<string, EntitySighting> _sightings = new();

    public IReadOnlyDictionary<string, EntitySighting> Sightings => _sightings;

    // Outcome of the agent's last decision (a primitive, helper or skill), as shown to the LLM.
    public string? LastOutcome { get; set; }
    public Cell? ExitPosition { get; private set; }

    public AgentMemory Clone()
    {
        var copy = new AgentMemory { LastOutcome = LastOutcome, ExitPosition = ExitPosition };
        foreach (var (id, s) in _sightings)
            copy._sightings[id] = s;
        return copy;
    }

    public EntitySighting? Get(string id) => _sightings.GetValueOrDefault(id);

    // Returns ids seen for the first time in this update.
    public IReadOnlyList<string> Update(DungeonWorld world)
    {
        if (world.IsVisible(world.Map.Exit))
            ExitPosition = world.Map.Exit;

        var newlySeen = new List<string>();
        void Record(EntitySighting s)
        {
            if (!_sightings.ContainsKey(s.Id))
                newlySeen.Add(s.Id);
            _sightings[s.Id] = s;
        }

        foreach (var guard in world.Guards.Where(g => world.IsVisible(g.Position)))
            Record(new EntitySighting(guard.Id, EntityKind.Guard, "guard", guard.Position, Lower(guard.State), world.Tick));

        foreach (var door in world.Doors.Where(d => world.IsVisible(d.Position)))
            Record(new EntitySighting(door.Id, EntityKind.Door, Lower(door.Kind), door.Position,
                door.IsOpen ? "open" : "closed", world.Tick));

        // The agent always knows its own inventory; floor items only when in sight.
        foreach (var item in world.Items)
        {
            if (item.IsHeld)
                Record(ItemSighting(item, null, "held", world.Tick));
            else if (world.IsVisible(item.Position!.Value))
                Record(ItemSighting(item, item.Position, "on_floor", world.Tick));
        }

        // Forget items that are gone: given away (only the agent can do that), or remembered
        // on a cell that is visible now and no longer holds them.
        var forget = _sightings.Values.Where(s => s.Kind == EntityKind.Item && ItemIsGone(world, s)).Select(s => s.Id).ToList();
        foreach (var id in forget)
            _sightings.Remove(id);

        return newlySeen;
    }

    private static bool ItemIsGone(DungeonWorld world, EntitySighting s)
    {
        var item = world.Items.FirstOrDefault(i => i.Id == s.Id);
        if (item is null)
            return true;
        return s.State == "on_floor" && world.IsVisible(s.Position!.Value) && item.Position != s.Position;
    }

    private static EntitySighting ItemSighting(Item item, Cell? position, string state, long tick) =>
        new(item.Id, EntityKind.Item, Lower(item.Type), position, state, tick, item.Opens);

    private static string Lower<T>(T value) where T : Enum => value.ToString().ToLowerInvariant();
}
