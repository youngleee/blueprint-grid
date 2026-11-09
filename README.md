# Action Space Expansion in an LLM Dungeon Agent

Master's semester project. An LLM agent explores a partially observable,
stochastic grid dungeon (guards, locked and jammed doors) and extends its own
action set with verified, parameterized, closed-loop skills. The research plan,
definitions and current status are in [`docs/PLAN.md`](docs/PLAN.md).

**Status:** the environment is implemented and tested; the agent, LLM
integration and skill system are not yet built. See the status table in
`docs/PLAN.md`.

## Project structure

| Path | Contents |
|---|---|
| `Dungeon/` | Environment library (no MARS dependency): seeded generator, world, entities, text rendering |
| `Dungeon.Cli/` | `play` and `render` commands for manual testing |
| `Dungeon.Tests/` | xUnit tests, incl. 1 000-seed property tests |
| `docs/PLAN.md` | Research plan, specification, timeline, implementation status |
| `docs/TESTING.md` | How to test the environment by hand |
| `GridBlueprint/` | **Previous prototype** (MARS detour agent), kept until the dungeon gets its MARS host |
| `Visualization/` | PyGame visualizer for MARS runs (currently for the old prototype) |

## Requirements

- .NET SDK 10.0 (pinned through `global.json`)
- Python 3.8+ for the visualization only

## Build and test

```bash
dotnet build GridBlueprint.sln
dotnet test Dungeon.Tests
```

## Try the environment

```bash
dotnet run --project Dungeon.Cli -- render --seed 1 --tier 3   # full dungeon + hidden parameters
dotnet run --project Dungeon.Cli -- play   --seed 1 --tier 3   # play as the agent
```

Details and manual test cases: [`docs/TESTING.md`](docs/TESTING.md).

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

## Secrets

LLM credentials go in the ignored `GridBlueprint/.env` (see `.env.example`).
Never commit API keys.
