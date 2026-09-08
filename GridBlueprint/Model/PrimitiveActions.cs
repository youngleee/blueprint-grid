namespace GridBlueprint.Model;

// Provides the trusted movement actions available at startup.
public static class PrimitiveActions
{
    public static readonly AgentAction MoveUp = new("move_up", 0, -1);
    public static readonly AgentAction MoveDown = new("move_down", 0, 1);
    public static readonly AgentAction MoveLeft = new("move_left", -1, 0);
    public static readonly AgentAction MoveRight = new("move_right", 1, 0);
}
