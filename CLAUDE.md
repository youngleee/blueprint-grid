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
    `explore`), `ActionSpace` (A_t: primitives + helpers, later skills),
    `Prompts`, `DecisionParser`, `Deciders` (`LlmDecider`, `RuleBasedDecider`),
    `EpisodeRunner`, `ExperimentRunner`.
  - `Llm/`: `OpenAiClient` (Responses API), `CachingClient` (record/replay),
    `MeteredClient` (call/token caps), `StubLlmClient` (offline), `LlmSettings`.
  - `Skills/`: `Skill` model, `SkillJson` (parse/serialize), `SkillInterpreter`
    (closed-loop execution + contracts), `SkillActions` (skill → action),
    `HandwrittenSkills` + `Library/handwritten.json` (embedded, conditions D/RD),
    `SkillValidator` (static checks, closed-loop / macro form), `ScenarioTester`
    (micro-room tests) and `SkillAdmission` (both + feedback text),
    `InducePrompts` (induce_skill prompt), `SkillInducer` + `InductionPolicy`
    (when to induce, feedback rounds, caps, registration).
- `Dungeon.Cli/`: `play`, `render`, `run`, `skills`, `replay`.
- `Dungeon.Tests/`: xUnit. `LlmFakes.cs` has the scripted client and HTTP stub.
  `Fixtures/calm_guard_v1|v2|v3|blind.json` are skill drafts with known
  full-episode quality; they calibrate the scenario tester (PLAN §6.4).
- `Dungeon.Mars/`: MARS host that **replays** a recorded episode
  (`EpisodeReplay`) into a `RasterLayer` for the PyGame visualizer. It never
  drives an experiment.
- `GridBlueprint/`: previous MARS prototype (detour agent). Do not extend it.
- `Visualization/`: PyGame visualizer for MARS runs (`main.py dungeon` for
  the replay, plain `main.py` for the old prototype).
- `scripts/`: `run_protocol.sh` (experiment phases; skips finished runs),
  `analyze.py` + `test_analyze.py` (report tables and statistics).
- `docs/EXPERIMENTS.md`: how to run the study on another machine.

## Build, test, run

```bash
dotnet build GridBlueprint.sln
dotnet test Dungeon.Tests
dotnet run --project Dungeon.Cli -- play --seed 1 --tier 3
dotnet run --project Dungeon.Cli -- run --condition A --provider stub --seeds 1-3
```

Options and output layout: `docs/TESTING.md` → *Experiment runs*.

## Invariants

- **Determinism.** All randomness goes through `Rng` streams derived from the
  seed (`layout`, `entity`, `outcome`; `layout#n` / `entity#n` only for the
  rare seeds whose first layout is unusable). Never use `System.Random`,
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
- **One path for decisions.** CLI and LLM both act through
  `ActionSpace.Execute`. Entity arguments are validated against memory, and the
  error for an unseen id must equal the error for a non-existent one.
- **Prompts are cache keys.** Any change to prompt text, model, temperature or
  max tokens changes the cache key and makes old recordings unreplayable.
  That is intended; mention it in the commit message.
- **Skills are data.** A skill is JSON interpreted by `SkillInterpreter`; it is
  never compiled or executed as code. Conditions are evaluated on a fresh
  `FactSet` after every step (that is what makes skills closed-loop). Steps go
  through `ActionSpace.Invoke`, so a skill can only do what the agent can do.
- **Changing `handwritten.json` changes results.** It is the D/RD reference;
  rerun R vs RD and update the table in `docs/PLAN.md` §9.1 in the same commit.
- **Admission thresholds are calibrated, not chosen.** `ScenarioTestOptions`
  defaults (20 rooms, ≥ 16 passes, 0 lost episodes) come from the fixture
  calibration in PLAN §6.4; `ScenarioTesterTests` pins the counts. Changing
  the tester, the room generator or the defaults means re-running that
  calibration and updating §6.4.
- **Traces must be complete.** `EpisodeReplay` re-executes an episode from
  its trace alone. Anything that affects an episode must be recoverable from
  `run.json`, the traces and `induction.jsonl`; a change that breaks
  `replay --check` on fresh runs is a bug.
- **Induction never executes model output.** A reply becomes a `Skill` only
  through `SkillJson`, and joins `A_t` only after `SkillAdmission` accepts it.
  Prompt changes change cache keys (see above).
- **Condition semantics live in `ExperimentRunner`.** A/R/D/RD: fixed action
  set per episode. B: induction, library reset each episode. C/E: induction,
  library persists; E uses `SkillForm.Macro`. `--freeze` disables induction.
- **Model output is untrusted, always.** Refusals, empty or truncated replies
  are replies (one decision / one attempt), never exceptions. Only transport
  failures and non-Responses bodies are `LlmException`. Library files are
  untrusted too: load them with `SkillJson.LoadLibrary`.
- **Runs are append-only directories.** Never write into a non-empty `--out`;
  `run.json` must exist even for interrupted runs (`aborted_reason`).
- **New robustness bugs get a test in `RobustnessTests.cs`** and a row in PLAN
  §14.1.
- **No network in tests.** Use `ScriptedLlmClient`, `StubHandler` or
  `StubLlmClient`. Never log or persist the API key.
- **Clone is deep.** Every new piece of mutable world state must be copied in
  the `DungeonWorld` copy constructor and covered by a clone test.
- **Solvable by construction.** Obstacles sit on chokepoints in chain order;
  each required item is reachable before its obstacle. Keep the property tests
  in `ScenarioGeneratorTests` green over 1 000 seeds.
- The LLM only proposes; code validates and executes. Generated code is never
  executed directly.

## Editing rules

- Small commits, `GP-### type(scope): imperative description`.
- Build and run `dotnet test Dungeon.Tests` after C# changes. CI
  (`.github/workflows/ci.yml`) repeats that plus `scripts/test_analyze.py`,
  `skills --test` and a stub protocol dry run with `verify`; keep it green.
- **No stale docs:** update `docs/PLAN.md` (status table and any changed
  specification), `docs/TESTING.md`, `README.md` and this file in the same
  commit as the code change they describe.
- Do not commit `.env`, `runs/`, `cache/`, logs, skill archives, `bin/` or `obj/`.
