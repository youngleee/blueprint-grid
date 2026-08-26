# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

This is a starter/template model for the [MARS](https://www.mars-group.org/) agent-based simulation framework (`Mars.Life.Simulations`), demonstrating a grid-based simulation: agents move on a 2D grid, interact with the grid's cell data, and interact with each other. It is meant to be used as a base to build custom grid-based MARS models on top of.

The repository has two independent parts that communicate over a websocket:

- `GridBlueprint/` — the .NET simulation (the actual model).
- `Visualization/` — a standalone Python/PyGame tool that visualizes the running simulation.

## Build & run

Requires .NET SDK 10.0 (pinned in `global.json`, `rollForward: latestMajor`). Uses the `Mars.Life.Simulations` NuGet package, pinned to `6.0.0` in `GridBlueprint.csproj` (previously a floating `5.*`) — bumping this is a MARS major-version upgrade and can change runtime behavior of framework APIs (see the `MoveWithBearing` gotcha below for a case that broke on the 5→6 upgrade).

```bash
dotnet build GridBlueprint.sln          # build
dotnet run --project GridBlueprint      # run the simulation (reads GridBlueprint/config.json)
```

There is no test project in this repository.

To also see the simulation visualized, before running the simulation:

```bash
cd Visualization
pip3 install -r requirements.txt   # Note: requirements.txt pins older pygame/websocket-client;
                                    # on newer Python you may need to relax those pins
python3 main.py                    # opens a PyGame window; leave it running
```

Then run the model with `pythonVisualization: true` set in `config.json` (see below). The simulation pushes tick-based `GridLayer` state over a websocket to this tool. Speed is controlled in the PyGame window with the up/down arrow keys.

The intended IDE workflow (per README) is JetBrains Rider for the C# side, running `GridBlueprint/Program.cs`'s `Main()` directly.

## Architecture

### Simulation bootstrap (`Program.cs`)

The model is composed, not hardcoded: `Main()` builds a `ModelDescription` by registering one layer type (`GridLayer`) and three agent types (`SimpleAgent`, `ComplexAgent`, `HelperAgent`), then loads a `SimulationConfig` from `config.json` at the working directory and hands both to `SimulationStarter.Start(...)`. Agent counts, start positions, and which grid/agent resource files get loaded are all driven by `config.json` + the CSV files in `Resources/`, not by code — to change what runs, edit the config/resources rather than `Program.cs`.

### Layer/agent relationship

- `GridLayer` (`Model/GridLayer.cs`) extends MARS's `RasterLayer` and is the environment agents live on. `InitLayer` spawns the configured agents via `IAgentManager` and creates a `SpatialHashEnvironment<T>` per agent type (`SimpleAgentEnvironment`, `ComplexAgentEnvironment`) for spatial indexing/movement. `IsRoutable(x, y)` treats cell value `0` as walkable and anything else as blocked — this is what both pathfinding (`_layer.FindPath`) and manual bounds/collision checks rely on.
- Each agent type implements `IAgent<GridLayer>` + `IPositionable` with an `Init(GridLayer)` (called once) and `Tick()` (called every simulation step). Agents read their per-instance start parameters (`StartX`, `StartY`, etc.) from `[PropertyDescription(Name = "...")]`-annotated properties, populated from the agent's CSV file in `Resources/` (column headers must match the `PropertyDescription` names).
- Agents insert themselves into their type's `SpatialHashEnvironment` on `Init` and must call `MoveTo`/`Remove` on that same environment (not just mutate `Position`) to keep the spatial index and the visualization in sync.

### Agent types

- `SimpleAgent`: moves to a random routable adjacent cell each tick (or stays in place if blocked), removes itself from the simulation at tick 595.
- `ComplexAgent`: each tick randomly picks a new `AgentState` (`Model/AgentState.cs`) unless a `MoveTowardsGoal` trip is still in progress, then dispatches to the matching movement method:
  - `MoveRandomly` — same as `SimpleAgent`.
  - `MoveWithBearing` — computes a bearing toward an explored nearby cell and moves one step via `SpatialHashEnvironment.MoveTowards`, rolling back if the destination turns out not routable. Skips the move entirely if the explored goal is the agent's own current cell (see gotcha below).
  - `MoveTowardsGoal` — on starting a trip, explores nearby routable cells up to `MaxTripDistance`, picks a goal, computes a path with `_layer.FindPath`, then advances one path step per tick until the goal is reached.
  - `ExploreAgents` — looks for nearby `SimpleAgent` instances within `AgentExploreRadius` and increments their `MeetingCounter` if adjacent (Chebyshev distance ≤ 1).
- `HelperAgent`: not a "real" agent — it exists purely so the simulation performs a write to the `GridLayer` every tick (`_layer[0,0] = _layer[0,0]`), which is what triggers MARS to push layer data to the visualization websocket (data is only sent on change). It is excluded from the visualization display itself.
- `MovementDirections` (`Model/MovementDirections.cs`): the eight `Position` offsets (N/NE/E/SE/S/SW/W/NW) shared by both movable agent types for random movement.

### Configuration (`GridBlueprint/config.json`)

- `globals.startTime`/`endTime`/`deltaT(Unit)` define simulation duration/resolution; `output` controls result output format; `pythonVisualization` toggles the websocket feed to `Visualization/`.
- `layers[]` and `agents[]` list which layer/agent types to include, their `count`, and the `Resources/*.csv` file supplying their init parameters. Multiple grid CSVs of different shapes/obstacle patterns are provided in `Resources/` (`grid.csv`, `grid_2x2.csv`, `grid_50x25.csv`, `grid_50x50.csv`, `grid_closed.csv`) — swap `layers[0].file` to change the map.
- The JSON schema validator (if enabled in-IDE) expects full ISO-8601 datetimes; the short form (`"2022-03-01T05:00"`) still runs fine, it just won't pass strict schema validation.

## Known gotchas

- `ComplexAgent.FindRoutableGoal()` (used by both `MoveWithBearing` and `MoveTowardsGoal`) falls back to returning the agent's *own current cell* as the "goal" when it's the only routable cell within range (e.g. the agent is boxed in by obstacles). Any new caller of `FindRoutableGoal()` must handle `goal.Equals(Position)` explicitly:
  - `MoveWithBearing` checks for this and skips the move, since a zero-length vector makes `PositionHelper.CalculateBearingCartesian` return `NaN`, and `SpatialHashEnvironment.MoveTowards` (as of MARS 6.0.0) throws `ArgumentException: Bearing is not a number` on a `NaN` bearing — this is what broke when the package was bumped from the floating `5.*` to pinned `6.0.0`.
  - `MoveTowardsGoal` doesn't hit this problem the same way since `_layer.FindPath(Position, Position)` degenerates to a trivial no-op path, but a related infinite-loop edge case there was fixed separately (see commit `7fb8dab`).

## Adding a new agent or layer type

Follow the existing pattern: create the class under `Model/` implementing `IAgent<GridLayer>` + `IPositionable` (with `Init`/`Tick`), add a corresponding `Resources/*.csv` with a header row matching its `[PropertyDescription]` properties, register it in `Program.cs` via `description.AddAgent<T, GridLayer>()`, spawn it from `GridLayer.InitLayer`, and add an entry under `agents` in `config.json` pointing at its CSV.
