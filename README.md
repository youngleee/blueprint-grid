# Adaptive MARS Agent

This repository contains a grid-based agent simulation built with MARS. The main experiment is an `AdaptiveAgent` that uses trusted primitive movements, validated composite skills, and an optional LLM to propose a detour when it encounters an obstruction.

The LLM never executes code directly. It proposes JSON containing primitive moves; `ActionValidator` and the agent's path checks decide whether the proposal can be registered and executed. The offline `FakeLlmClient` makes demonstrations deterministic. The `OpenAiLlmClient` is available for real-model experiments.

## Project structure

- `GridBlueprint/`: .NET 10 MARS simulation.
- `GridBlueprint/Model/AdaptiveAgent.cs`: adaptive navigation and detour handling.
- `GridBlueprint/Model/ActionValidator.cs`: validates generated skills before registration.
- `GridBlueprint/Model/SkillArchive.cs`: persists validated skills.
- `GridBlueprint/Model/LlmSkillGenerator.cs`: parses provider responses into structured skills.
- `GridBlueprint/config.adaptive.detour.json`: headless two-wall detour scenario.
- `GridBlueprint/config.adaptive.visual.json`: 20×20 visualization scenario.
- `Visualization/`: standalone Python/PyGame visualization.
- `thomas_readme.md`: short German guide for running the demo.

## Requirements

- .NET SDK 10.0 or higher, pinned through `global.json`.
- `Mars.Life.Simulations` 6.0.0, restored automatically by .NET.
- Python 3.8 or higher for visualization. Python 3.13 users should use a compatible PyGame release listed in `Visualization/requirements.txt`.

## Build and run

```bash
dotnet build GridBlueprint.sln
cd GridBlueprint
ADAPTIVE_LLM_PROVIDER=fake dotnet run -- config.adaptive.detour.json
```

The demo starts at `(1,10)`, reaches the first goal at `(9,10)`, detours around the first wall, then reuses the validated detour at the second wall before reaching `(18,10)`.

The `GridBlueprint/run.sh` helper builds the project, runs a scenario, writes a timestamped log to `GridBlueprint/logs/`, and opens the log in the default macOS text editor:

```bash
cd GridBlueprint
bash run.sh config.adaptive.visual.json
```

## LLM providers

The default provider is `fake`. To use OpenAI, create `GridBlueprint/.env`:

```text
ADAPTIVE_LLM_PROVIDER=openai
OPENAI_API_KEY=your-api-key
OPENAI_MODEL=gpt-4.1
```

The local `.env` file is ignored by Git. Prompts, candidates, rejections, provider information, and token usage where available are written to the run log; secrets are not logged.

The LLM is called only after the agent encounters a blocked cell and cannot reuse an applicable validated skill. It is a proposal mechanism, not the safety layer or the low-level movement controller.

## Visualization

```bash
cd Visualization
python3 -m venv .venv
source .venv/bin/activate       # macOS/Linux
pip install -r requirements.txt
python main.py
```

In a second terminal, run the simulation with `config.adaptive.visual.json`. The PyGame window shows the 20×20 grid, red blocked cells, the agent, the first goal, and the final goal. Use the up/down arrow keys to change visualization speed.

## Benchmark

```bash
cd GridBlueprint
dotnet run -- --benchmark
```

The output is written to `GridBlueprint/benchmark-results.csv`. The current benchmark is an MVP benchmark for skill generation and reuse; Phase 2 will add a classical A*/`FindPath` baseline, obstacle scenarios, real-model repetitions, parameterized skills, and per-task metrics.

## Current research limitation

For a fully observable, deterministic grid with coordinate goals, classical search is the natural baseline and may be superior to an LLM. Phase 2 therefore tests whether an LLM adds value under conditions such as partial observability, semantic goals, or reusable strategies across changing obstacle layouts. If A* or a simpler rule-based system performs better, that is the correct conclusion.

## Configuration

Simulation configuration is loaded from JSON rather than hard-coded in `Program.cs`. Available resources include multiple grid CSVs and agent CSVs under `GridBlueprint/Resources/`. Change the scenario file or its resource references to select a different grid.
