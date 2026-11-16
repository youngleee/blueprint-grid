# Visualization

PyGame viewer for MARS runs over the MARS websocket (`ws://127.0.0.1:4567/vis`).

## Install

Python 3.8 or newer:

```bash
cd Visualization
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
```

## Dungeon replay (current project)

Watch a recorded experiment episode. Terminal 1:

```bash
python main.py dungeon
```

Terminal 2, from the repository root:

```bash
dotnet run --project Dungeon.Mars -- --run runs/protocol/test_C_frozen --seed 1001 [--reveal]
```

One decision per MARS tick. Colours: red wall, yellow entity in sight (guard, door, item), purple
remembered floor, blue exit, white floor in sight or unknown; the dot is the agent. Without
`--reveal` it shows only what the agent knew. The terminal prints each decision and marks the
decision where a skill was learned. Up/down arrows change the frame rate.

The same replay without a window: `dotnet run --project Dungeon.Cli -- replay --run DIR --seed N --delay 300`.

## Detour prototype (GridBlueprint)

```bash
python main.py
# second terminal:
cd GridBlueprint && bash run.sh config.adaptive.visual.json
```

Any MARS model shows up if its configuration sets `"pythonVisualization": true` in `globals` and on
the layer.
