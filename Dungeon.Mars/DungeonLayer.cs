using Dungeon.Agent;
using Dungeon.Core;
using Dungeon.Render;
using Mars.Components.Layers;
using Mars.Core.Data;
using Mars.Interfaces.Data;
using Mars.Interfaces.Layers;

namespace Dungeon.Mars;

// MARS host for a recorded dungeon episode (PLAN §5.1): the experiment runs headless in the
// Dungeon library; this layer only shows a replay of it. Every tick the raster is rewritten from
// the replay frame, so the existing PyGame websocket visualizer draws it unchanged.
//
// Raster values (Visualization/main.py RASTER_COLORS): 1 wall (red), 2 entity in sight (yellow),
// 3 remembered floor (purple), 4 exit (blue), 0 floor in sight / unknown (white).
public class DungeonLayer : RasterLayer
{
    public static ReplayResult? Replay { get; set; }
    public static bool Reveal { get; set; }

    public int Frame { get; private set; } = -1;

    public override bool InitLayer(LayerInitData layerInitData, RegisterAgent registerAgentHandle,
        UnregisterAgent unregisterAgentHandle)
    {
        var ok = base.InitLayer(layerInitData, registerAgentHandle, unregisterAgentHandle);
        layerInitData.Container.Resolve<IAgentManager>().Spawn<ReplayAgent, DungeonLayer>().ToList();
        return ok;
    }

    public bool Advance()
    {
        if (Replay is null || Frame + 1 >= Replay.Frames.Count)
            return false;
        Frame++;
        var f = Replay.Frames[Frame];
        Draw(f.World);
        Console.WriteLine($"decision {f.Decision}{(f.Learned is null ? "" : $" [learned {f.Learned}]")}: {f.Outcome}");
        return true;
    }

    private void Draw(DungeonWorld world)
    {
        for (var x = 0; x < Width && x < world.Map.Width; x++)
            for (var y = 0; y < Height && y < world.Map.Height; y++)
            {
                // MARS rasters have y pointing up; the dungeon's y points down.
                var glyph = AsciiRenderer.Glyph(world, new Cell(x, y), Reveal);
                this[x, Height - 1 - y] = glyph switch
                {
                    '#' => 1,
                    'X' => 4,
                    ',' => 3,
                    '.' or ' ' or '@' => 0,
                    _ => 2
                };
            }
    }

    public override bool IsRoutable(int x, int y) => this[x, y] != 1;
}
