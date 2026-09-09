# MARS Adaptive Agent

## Watch the demo

Requires .NET 10 and Python 3.13. Start both terminals in the repository folder.

The visualization scenario uses a 20×20 grid with red obstacle cells.

The agent starts at `(1,10)`. Yellow marks the first goal at `(9,10)`.

Green marks the final goal at `(18,10)`.

The light-blue dot is the agent.

Terminal 1:

```bash
cd Visualization
python3.13 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
python main.py
```

Terminal 2:

```bash
cd GridBlueprint
bash run.sh config.adaptive.visual.json
```

After each run, the log opens in your text editor on macOS. Logs are saved in `GridBlueprint/logs/`.

Look for `blocked at`, `Generating skill`, `Registered validated detour`, then `Reusing validated detour` at the second wall and `reached its goal`.

## Skill generation

A blocked move triggers a search for a stored detour that legally reaches the goal. If none exists, the generator receives the grid bounds, obstacles and movement rules. Every step is checked before registration. The second wall tests reuse at a different position.

Skills are limited to 8 primitive moves. Invalid candidates are discarded; after 3 failed attempts the task reports failure. This demo needs only a 4-move detour.

Without configuration, the generator returns a canned demo detour. For real generation, create `GridBlueprint/.env` from `.env.example`, select `ADAPTIVE_LLM_PROVIDER=openai` and fill in your API key, then run the same demo command. The log names the provider and includes the prompt.

To request a fresh skill, move `GridBlueprint/bin/Debug/net10.0/skills.json` aside before running. Otherwise a saved detour can be reused immediately. Primitive movements can physically take this route; the missing capability is planning a detour in the current greedy agent.

## Benchmark

```bash
cd GridBlueprint
dotnet run -- --benchmark
```
