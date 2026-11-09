# CLAUDE.md

Guidance for coding agents working in this repository.

## Project

Master's project: an LLM agent in a seeded, partially observable, stochastic
grid dungeon that extends its own action space with verified skills. The plan,
specification and **implementation status** are in `docs/PLAN.md`; read it
before changing behaviour.

## Layout

- `Dungeon/`: environment library, plain C#, no MARS dependency.
  - `Core/`: `Rng` (SplitMix64, named streams), `Generator` (rooms/corridors),
    `ScenarioGenerator` (obstacles on chokepoints + required items),
    `DungeonWorld` (turn-based state, primitives, entity updates, `Clone()`),
    `Visibility`.
  - `Entities/`: `Guard`, `Door`, `Item`.
  - `Render/`: `AsciiRenderer` (agent view / reveal / debug),
    `CommandInterpreter` (text commands → primitives, helpers, inspection).
  - `Agent/`: `AgentMemory` (last-known entity states), `AgentContext` (world +
    memory; the only way agent-side code acts), `FactSet` (what the LLM sees),
    `Condition` (precondition/effect language), `Navigation` (`go_to`,
    `explore`).
- `Dungeon.Cli/`: `play` and `render` for manual testing.
- `Dungeon.Tests/`: xUnit.
- `GridBlueprint/`: previous MARS prototype (detour agent). Do not extend it;
  it will be replaced by a MARS host for `DungeonWorld`.
- `Visualization/`: PyGame visualizer for MARS runs.

## Build, test, run

```bash
dotnet build GridBlueprint.sln
dotnet test Dungeon.Tests
dotnet run --project Dungeon.Cli -- play --seed 1 --tier 3
```

## Invariants

- **Determinism.** All randomness goes through `Rng` streams derived from the
  seed (`layout`, `entity`, `outcome`). Never use `System.Random`,
  `Guid.NewGuid()` or time in world logic. Changing stream derivation breaks the
  pinned test in `RngTests` and invalidates recorded runs and LLM caches.
- **Turn-based.** A valid primitive costs exactly one tick; `invalid` actions
  cost none. Entities update after the agent's action in the same tick.
- **No information leaks.** Anything shown to the agent (and later the LLM)
  must come from `AgentMemory` / `FactSet`, never from `DungeonWorld` entity
  lists directly. Agent-side code acts through `AgentContext.Do` (primitive)
  and `AgentContext.Decide` (one decision). `reveal` / `debug` output is for
  humans only. `FactSetTests` checks this over 1 000 seeds.
- **Helpers plan on beliefs.** `Navigation` uses only known cells and
  remembered entity states, and must interrupt on anything decision-relevant.
- **Clone is deep.** Every new piece of mutable world state must be copied in
  the `DungeonWorld` copy constructor and covered by a clone test.
- **Solvable by construction.** Obstacles sit on chokepoints in chain order;
  each required item is reachable before its obstacle. Keep the property tests
  in `ScenarioGeneratorTests` green over 1 000 seeds.
- The LLM only proposes; code validates and executes. Generated code is never
  executed directly.

## Editing rules

- Small commits, `GP-### type(scope): imperative description`.
- Build and run `dotnet test Dungeon.Tests` after C# changes.
- **No stale docs:** update `docs/PLAN.md` (status table and any changed
  specification), `docs/TESTING.md`, `README.md` and this file in the same
  commit as the code change they describe.
- Do not commit `.env`, logs, skill archives, LLM caches, `bin/` or `obj/`.
