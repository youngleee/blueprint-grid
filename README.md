# Action Space Expansion in an LLM Dungeon Agent

Master's semester project. An LLM agent explores a partially observable,
stochastic grid dungeon (guards, locked and jammed doors) and extends its own
action set with verified, parameterized, closed-loop skills. The research plan,
definitions and current status are in [`docs/PLAN.md`](docs/PLAN.md).

**Status:** environment, agent observation layer, LLM integration (client,
record/replay cache, budget) and the baseline conditions A (LLM, primitives
only) and R (hand-written rules) are implemented and tested offline. The skill
system is next. See the status table in `docs/PLAN.md`.

## Project structure

| Path | Contents |
|---|---|
| `Dungeon/` | Library (no MARS dependency): `Core/` world and generator, `Entities/`, `Agent/` memory, facts, conditions, navigation, action space, deciders, runners, `Llm/` OpenAI client, cache, budget, `Render/` text views |
| `Dungeon.Cli/` | `play`, `render` and `run` commands |
| `Dungeon.Tests/` | xUnit tests, incl. 1 000-seed property tests; no test uses the network |
| `docs/PLAN.md` | Research plan, specification, timeline, implementation status |
| `docs/TESTING.md` | How to test by hand and how to run experiments |
| `GridBlueprint/` | **Previous prototype** (MARS detour agent), kept until the dungeon gets its MARS host |
| `Visualization/` | PyGame visualizer for MARS runs (currently for the old prototype) |

## Requirements

- .NET SDK 10.0 (pinned through `global.json`)
- An OpenAI API key for live LLM runs only
- Python 3.8+ for the visualization only

## Build and test

```bash
dotnet build GridBlueprint.sln
dotnet test Dungeon.Tests
```

## Try it

```bash
dotnet run --project Dungeon.Cli -- render --seed 1 --tier 3   # full dungeon + hidden parameters
dotnet run --project Dungeon.Cli -- play   --seed 1 --tier 3   # play as the agent
dotnet run --project Dungeon.Cli -- run --condition R --tier 3 --seeds 1-20              # rule baseline, no LLM
dotnet run --project Dungeon.Cli -- run --condition A --tier 3 --seeds 1-5 --provider stub  # LLM pipeline, offline
dotnet run --project Dungeon.Cli -- run --condition A --tier 3 --seeds 1-5               # real model (needs key)
```

Details, manual test cases and run outputs: [`docs/TESTING.md`](docs/TESTING.md).

## Secrets

Copy `.env.example` to `.env` (repository root) and set `OPENAI_API_KEY`.
`.env` is ignored by git; the key is never written to logs, traces or
`run.json`. The previous prototype reads its own `GridBlueprint/.env`.

## Previous prototype

`GridBlueprint/` contains the first prototype: an `AdaptiveAgent` that asked an
LLM for fixed detour sequences around walls. It was judged not to demonstrate
action space expansion (fixed macros in a fully observable, deterministic grid;
see `docs/PLAN.md` §1). The tag `v0-detour-prototype` marks its last state. Its
run instructions:

```bash
cd GridBlueprint
ADAPTIVE_LLM_PROVIDER=fake dotnet run -- config.adaptive.detour.json
```
