# MARS grid-based Model Starter

This model includes some basic building blocks for designing grid-based models in MARS. The environment of a grid-based model consists of a two-dimensional grid. Agents can move on the grid and interact with information in the grid cells as well as with each other.

## Project structure

Below is a brief description of each component of the project structure:

- `Program.cs`: the entry point of the model from which the model can be started. See [Model setup and execution](#model-setup-and-execution) below for more details.
- `config.json`: a JavaScript Object Notation (JSON) with which the model can be configured. See [Model configuration](#model-configuration) below for more details.
- `Model`: a directory that holds the agent types, layer types, entity types, and other classes of the model. See [Model description](#model-description) for more details.
- `Resources`: a directory that holds the external resources of the model that are integrated into the model at runtime. This includes initialization and parameterization files for agent types and layer types.

## Model description

The model consists of the following agent types and layer types:

- Agent types:
  - `SimpleAgent`: an agent that can move randomly.
  - `ComplexAgent`: an agent that can move in different ways (randomly, bearing-based, goal-oriented), plan trips, and interact with `SimpleAgent` instances. These behaviors are guided by a set of `AgentState`.
  - `HelperAgent`: an agent that is implemented only for technical reasons. Data from the `GridLayer` is sent to the web socket of the visualization tool only when that data is changed. The `HelperAgent` performs a change to the `GridLayer` data to enable the visualization of the grid. Agents of this type are not displayed in the visualization component.
- Layer types:
  - `GridLayer`: the layer on which the agents live and move.
- Other classes:
   - `MovementDirections`: a static helper class exposing eight movement directions as `Position` constants. Each direction changes an agent's position by one unit horizontally and/or vertically.
   - `AgentState`: an enumeration of agent states that guide agent behavior (`MoveRandomly`, `MoveWithBearing`, `MoveTowardsGoal`, `ExploreAgents`).

## Model configuration

The model can be configured via a JavaScript Object Notation (JSON) file called `config.json`. Below are some of the main configurable parameters:

- `startTime` and `endTime`: the start time and end time, respectively, of the simulation
- `deltaT`: the length of a single time step. The simulation time is given by the number of `deltaT` time steps that fit into the range defined by `startTime` and `endTime`
- `pythonVisualization`: a boolean flag that, if set to `true`, prompts the simulation framework to send tick-based data to an external web socket, where it is further processed by a visualization tool. See below for how to set up the visualization tool and get a visualized simulation output.
- `layers`: the layer types that should be included in the simulation
  - In the directory `Resources`, some exemplary layer input files are provided. To use them, please update the `file` key of the `GridLayer` object in the JSON file.
- `agents`: the agent types that should be included in the simulation
  - The number of agents can be changed here by updating the value of the `count` key of each agent type.

For more detailed information on configuration parameters, see the [MARS documentation](https://www.mars-group.org/docs/tutorial/configuration/sim_config_options/).

## Model setup and execution

The following tools are required on your machine to run a full simulation and visualization of this model:

- A C# IDE, preferably JetBrains Rider
- .NET SDK **10.0** or higher (pinned via `global.json`)
- The `Mars.Life.Simulations` NuGet package, pinned to version **6.0.0** in `GridBlueprint.csproj` (restored automatically on build)
- Python 3.8 or higher (note: `Visualization/requirements.txt` pins older `pygame`/`websocket-client`; on newer Python you may need to relax those pins)


To set up and run the simulation and visualization, please follow these steps:

1. Download or clone this repository
2. Navigate into the folder of this `README.md` in your terminal
3. Follow these instructions to start the visualization tool (alternatively, see the README in the `Visualization` directory):
    1. Open a terminal in the `Visualization` directory
    2. Execute the following command:
        1. macOS: `pip3 install -r requirements.txt`
        2. Windows: `pip install -r requirements.txt`
    3. Once the installation has finished, execute the following command:
        1. macOS: `python3 main.py`
        2. Windows: `python main.py`
    4. A black PyGame window should open. **Note:** Do not close the terminal.
    5. Alternatively to the above, the visualization tool can be started with a Python IDE such as [JetBrains PyCharm](https://www.jetbrains.com/pycharm/).
4. Open JetBrains Rider.
5. Open the solution file `GridBlueprint.sln`.
6. Run the `Main()` method in `GridBlueprint/Program.cs`. The model loads its configuration from `config.json` (external JSON), not from code.
7. The simulation should run in Rider and, simultaneously, a visualization should be displayed in the PyGame window.

## Interacting with the visualization

While the visualization is running, its speed (framerate) can be changed by pressing the up arrow (increase speed) or down arrow (decrease speed) on your keyboard.

## Optional: make `config.json` pass strict schema validation

If you enable the JSON schema in your IDE (per the MARS Installation guide), change the datetimes to full ISO-8601 to silence the validator:

```json
"startTime": "2022-03-01T05:00:00.000Z",
"endTime":   "2022-03-01T05:10:00.000Z"
```

(The short form runs fine at runtime; this only affects strict schema validation.)