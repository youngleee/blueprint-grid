# CLAUDE.md

This file provides guidance to coding agents working in this repository.

## Project overview

This is a .NET 10 model built with the MARS agent-based simulation framework. The current project is an adaptive grid-navigation experiment, not only the original MARS starter template.

The repository has two parts:

- `GridBlueprint/`: the C# simulation and adaptive agent.
- `Visualization/`: a standalone Python/PyGame websocket visualizer.

## Build and run

The project uses .NET SDK 10.0 and `Mars.Life.Simulations` 6.0.0.

```bash
dotnet build GridBlueprint.sln
cd GridBlueprint
ADAPTIVE_LLM_PROVIDER=fake dotnet run -- config.adaptive.detour.json
bash run.sh config.adaptive.visual.json
dotnet run -- --benchmark
```

`run.sh` builds first, records output under `GridBlueprint/logs/`, and opens the completed log on macOS. The benchmark writes `GridBlueprint/benchmark-results.csv`.

There is currently no automated test project. Adding unit tests for the action model, validator, archive, and adaptive-agent path checks is a Phase-2 task.

## Adaptive-agent architecture

`Program.cs` registers `GridLayer`, the existing demo agents, `AdaptiveAgent`, and `HelperAgent`, then loads the selected JSON configuration.

`AdaptiveAgent`:

- moves using registered cardinal primitive actions;
- detects when the next cell is blocked;
- first searches for an applicable validated composite skill;
- calls `LlmSkillGenerator` only when a blocked route has no reusable skill;
- validates generated JSON through `ActionValidator` and full path checks;
- executes accepted primitive steps one per simulation tick;
- persists accepted skills through `SkillArchive`;
- reports generation, reuse, LLM-call, and failure counts.

The LLM is a proposal mechanism. It does not execute arbitrary code and does not bypass movement, boundary, obstacle, endpoint, or skill-length checks.

## Providers and local configuration

The default provider is the deterministic `FakeLlmClient`. Real OpenAI runs use `OpenAiLlmClient` and are selected through the ignored `GridBlueprint/.env` file:

```text
ADAPTIVE_LLM_PROVIDER=openai
OPENAI_API_KEY=your-api-key
OPENAI_MODEL=gpt-4.1
```

Never commit `.env` or expose the API key in logs, source files, or benchmark artifacts.

## Important model files

- `Model/AdaptiveAgent.cs`: adaptive movement, obstruction handling, and detour execution.
- `Model/ActionRegistry.cs`: primitive and composite action registration.
- `Model/ActionValidator.cs`: structural and primitive-reference validation.
- `Model/CompositeAction.cs`: reusable skill representation and step limit.
- `Model/LlmSkillGenerator.cs`: provider response parsing.
- `Model/OpenAiLlmClient.cs`: OpenAI provider.
- `Model/FakeLlmClient.cs`: deterministic offline provider.
- `Model/SkillArchive.cs`: JSON persistence for validated skills.
- `Model/GridLayer.cs`: grid data, routability, movement, and MARS path support.
- `Visualization/main.py`: websocket consumer and PyGame display.

## Research direction and baseline

For a fully observable deterministic grid with coordinate goals, A* or MARS `FindPath` is the correct classical baseline and may outperform an LLM. Do not claim that the LLM is necessary for the current demo.

Phase 2 should test whether the LLM adds value for partial observations, semantic goals, or parameterized strategies that transfer across changing obstacle layouts. Every such claim must be compared with classical search and rule-based alternatives under the same observations, movement limits, and task set.

## Editing and verification rules

- Make small changes and use the `GP-### type(scope): imperative description` commit convention.
- Build after C# changes.
- Keep fake-provider demonstrations deterministic and offline.
- Validate every generated skill before registration or execution.
- Do not commit `.env`, generated logs, archives, benchmark outputs, `bin/`, or `obj/`.
- Preserve existing user changes and avoid destructive Git operations.
