namespace GridBlueprint.Model;

// Describes one trusted movement by name and coordinate delta.
public record AgentAction(string Name, int DeltaX, int DeltaY);
