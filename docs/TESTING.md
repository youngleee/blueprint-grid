# Testing the Dungeon Project

## Setup

```bash
brew install --cask dotnet-sdk          # .NET 10
dotnet --version                        # 10.x
dotnet build GridBlueprint.sln
```

## Automated tests

```bash
dotnet test Dungeon.Tests                                         # all (~35 s, 1000-seed property tests)
dotnet test Dungeon.Tests --filter "FullyQualifiedName~GuardTests"  # one area
```

No automated test uses the network or an API key.

## Manual tools

```bash
# Full dungeon incl. hidden entities and their chances (debug view)
dotnet run --project Dungeon.Cli -- render --seed 1 --tier 3

# Play as the agent (only what the agent can see is shown)
dotnet run --project Dungeon.Cli -- play --seed 1 --tier 3 [--sight 3] [--skills handwritten|FILE]

# Show a skill library: signatures, what the LLM is told, and the JSON
dotnet run --project Dungeon.Cli -- skills [--file FILE]

# Validate and scenario-test every skill in a library (20 generated rooms each)
dotnet run --project Dungeon.Cli -- skills --test [--file FILE] [--macro]

# Scripted run: pipe commands, output is reproducible for the same seed
printf 'state\nw\nw\nuse none door_1\n' | dotnet run --project Dungeon.Cli -- play --seed 1 --tier 3
```

Tiers: `1` one guard · `2` locked + jammed door · `3` all three.
In `play`, type `help` for the action list (generated from the action space,
so it is exactly what the LLM is offered). Every action goes through the same
validation as the LLM's choices. Command groups:

- **Primitives** (`move`, `wait`, `look`, `pick_up`, `talk`, `give`, `throw`,
  `use`): one tick each, none if `invalid`.
- **Helpers** (`go_to <id|exit> [distance]` — `goto` also works —, `explore`,
  `retreat <id> <distance>`): several ticks; `go_to` and `explore` stop early
  when something new comes into sight, a move fails, or a guard turns alert.
- **Skills** (only with `--skills`): `calm_guard <guard>`, `open_door <door>`.
  One decision; the output lists every step the skill took. A false
  precondition is `invalid` with the condition named; a missed effect is
  `failed` with the condition named.
- **Inspect** (free, no tick): `facts` (exactly what the LLM will see),
  `check <condition>` (evaluate a condition against the facts), `actions`,
  `map`, `state`, `reveal`, `debug`. `reveal` and `debug` show hidden
  information — the agent never gets these.

Arguments may be separated by spaces or written as a call: `give food_2 guard_1`
or `give(food_2, guard_1)`. Entity ids must be ones the agent has seen.
Map rows are labelled with `y`, the header gives the `x` range; `throw` takes
absolute `x y`.

## Manual test cases

Use `render` first to find the entity ids and positions for a seed.

| # | Case | Steps | Expected |
|---|---|---|---|
| 1 | Determinism | `render --seed 5` twice | identical output |
| 2 | Fog of war | `play`, walk away from start, `map` | remembered cells `,`, unknown blank, no entities on `,` |
| 3 | Closed door blocks sight | stand next to a closed door | door visible, cells behind it blank |
| 4 | Invalid is free | `use none <far door>` | `invalid`, tick unchanged |
| 5 | Key opens locked door | pick up key, go next to its door, `use key_N door_M` | `success`, door `/` |
| 6 | Wrong key | `use <other key> <door>` | `failed`, tick +1 |
| 7 | Jammed door | `use none <jammed door>` repeatedly | sometimes `failed`, eventually `success` |
| 8 | Guard patience | step within 2 of a guard, `wait` | `alert`, then `hostile`, episode failed |
| 9 | Retreat resets guard | get it alert, step out of range, `debug` | `idle`, patience full |
| 10 | Food distracts | `give food_N guard_1` adjacent | `distracted`, you can pass; later `alert` again |
| 11 | Throw from distance | from 2–3 cells, `throw food_N x y` within 4 of the guard | `distracted` without adjacency |
| 12 | Coin bribes permanently | `give coin_N guard_1` | stays `bribed` |
| 13 | Episode over | reach `X`, then `e` | `invalid` |
| 14 | Chances vary per seed | `render` seeds 1–5 | different calm/food/pick/push values |
| 15 | Facts show only what was seen | `facts` at start, compare with `reveal` | only in-sight entities; exit "not found yet" |
| 16 | Stale memory | see a guard go `alert`, step out of sight, `facts` vs `debug` | facts still say `alert`, debug says `idle` |
| 17 | Explore stops on discovery | `explore` repeatedly | each call ends with `spotted <id>` or `nothing left to explore` |
| 18 | goto stops next to doors | `goto door_N` | ends adjacent to the door, not on it |
| 19 | goto before seeing the exit | `goto exit` at the start | `invalid: the exit has not been seen yet`, tick unchanged |
| 20 | Last action reports the helper | `explore`, then `facts` | `last action: explore -> …`, not a single move |
| 21 | Conditions | `check has(key) and state(door_2) == closed`, `check state(guard_1)` | `true`/`false`; second is an error (needs a comparison) |
| 22 | Unseen ids are rejected | `talk guard_1` before seeing the guard | `invalid: unknown guard 'guard_1' (known: none)` — same as for a made-up id |
| 23 | Retreat resets a guard | get a guard `alert`, `retreat guard_1 3`, `debug` | guard `idle`, patience full |
| 24 | Skills are actions | `play --skills handwritten`, `actions` | lists `calm_guard(g)` and `open_door(d)` |
| 25 | Skill precondition | `play --seed 1 --tier 3 --skills handwritten`: `open_door door_1` at the start; later `open_door` on a door you have opened; on a locked door without key or lockpick | `invalid: unknown door 'door_1'` (not seen yet); `precondition not met: state(d) == closed`; `precondition not met: type(d) == jammed or has_key(d) or has(lockpick)` |
| 26 | calm_guard adapts | `play --tier 1 --skills handwritten` for seeds 3, 5, 8: `explore` until the guard is spotted, `calm_guard guard_1` | `success`; the step list differs per seed (e.g. one `talk`, or `go_to` then `talk`) |
| 27 | open_door picks the tool | `play --seed 1 --tier 2 --skills handwritten`: `goto key_1`, `pick_up`, `explore` until `door_1` (jammed) is spotted, `open_door door_1`, `explore` until `door_2` (locked), `open_door door_2` | steps show `use(none,door_1)` and `use(key_1,door_2)` |
| 28 | Library passes admission | `skills --test` | both skills `accepted`, ≥ 16/20 rooms, no lost episode |
| 29 | Weak draft is rejected | `skills --test --file Dungeon.Tests/Fixtures/calm_guard_v2.json` | `calm_guard: rejected … episode lost in 2`; 20 room lines with setup → outcome |
| 30 | Macro is rejected | `skills --test --file Dungeon.Tests/Fixtures/calm_guard_blind.json` | validator: `a fixed sequence of calls is a macro, not a skill` |

Probabilistic cases (7, 10–12) depend on the seed: the same seed and the same
command sequence always give the same outcome; a different seed may not.

## Experiment runs

```bash
dotnet run --project Dungeon.Cli -- run --condition A|R [options]
```

| Option | Default | Meaning |
|---|---|---|
| `--condition` | required | `A` LLM, primitives + helpers · `R` rules, no LLM · `D` LLM + hand-written skills · `RD` rules + hand-written skills · `B` LLM + induction, library reset per episode · `C` LLM + induction, persistent library · `E` as C with macros only |
| `--tier` | `3` | `1`, `2`, `3` |
| `--seeds` | `1-20` | `1-20`, `1,4,9`, `5` |
| `--max-decisions` | `40` | decision budget D_max per episode |
| `--llm` | `record` | `record` (cache, call on miss) · `replay` (cache only, offline) · `live` (always call) |
| `--provider` | from `.env`, else `openai` | `openai` · `stub` (offline, pipeline checks only) |
| `--max-calls`, `--max-tokens` | `2000`, `3000000` | hard caps on real API usage per run |
| `--out` | `runs/<time>_<cond>_tier<N>` | output directory |
| `--env` | `./.env` (or parent dirs) | settings file |
| `--skills` | D / RD: `handwritten`; C / E: `none` | D, RD: fixed library file instead of the built-in one. C, E: starting library (e.g. a previous run's `library.json`) |
| `--freeze` | off | B / C / E: no induction, library unchanged (held-out evaluation) |
| `--induce-attempts` | `3` | feedback rounds per induction request |
| `--induce-calls` | `6` | induction LLM calls per episode |
| `--admission` | `tested` | `tested`: validator + scenario tests must pass · `valid`: validator only (tests logged); sensitivity variant for E |

Output directory:

- `run.json` — configuration, decider, model, cache mode, budget, summary. Never the key.
- `metrics.csv` — one row per episode (columns listed in `docs/PLAN.md` §9.4).
- `traces/seed_N.jsonl` — one JSON line per decision: facts, action, args,
  reason, outcome, raw model reply, tokens, cache hit, skill steps, and any
  induction made before the decision.
- B / C / E only: `library.json` (skill library after the run; rewritten after
  each episode) and `induction.jsonl` (every induction request with all
  attempts, raw replies, feedback and tokens).

### Run cases

| # | Case | Command | Expected |
|---|---|---|---|
| R1 | Rule baseline | `run --condition R --tier 3 --seeds 1-200` | 175/200; failures are `caught by guard_1` or `decision_budget` |
| R1b | Skills help the rules | `run --condition RD --tier 3 --seeds 1-200` | 191/200, fewer decisions than R; `skill_invocations` > 0 in `metrics.csv` |
| R1d | Compare skill drafts | `run --condition RD --tier 3 --seeds 1-200 --skills Dungeon.Tests/Fixtures/calm_guard_v1.json` (then v2) | v1 195/200, v2 171/200 (built-in v3: 191/200) |
| R1c | Skill steps in traces | open any `traces/seed_N.jsonl` from R1b | skill lines have `"kind":"skill"` and a `skill_steps` list |
| R2 | Offline LLM pipeline | `run --condition A --provider stub --tier 3 --seeds 1-5` | runs, writes outputs; low success is expected (the stub only explores) |
| R3 | Replay is identical | R2, then the same with `--llm replay` | same `metrics.csv` except `cache_hits`; no network |
| R4 | Replay miss | `--llm replay` with seeds never recorded | stops with `replay cache miss`, `run.json` still written, exit code 1 |
| R5 | Missing key | `run --condition A` without `.env` | `error: OPENAI_API_KEY is not set`, exit code 2 |
| R6 | First live run | `.env` with key, `run --condition A --tier 3 --seeds 1-3 --max-calls 150` | 3 episodes; check `traces/` for valid JSON replies, tokens per decision in `metrics.csv` |
| R7 | Budget cap | R6 with `--max-calls 5` | stops after 5 real calls with `budget … reached` |
| R8 | Cache saves money | rerun R6 unchanged | all calls `cached`, no new tokens billed |
| R9 | Induction pipeline, offline | `run --condition C --provider stub --tier 3 --seeds 1-20 --out runs/stubC` | ~19/20; `skills induced 2`; `library.json` has 2 skills; `induction.jsonl` shows accepted and rejected attempts with feedback |
| R10 | B resets, C keeps | same with `--condition B` | similar success but ~40 skills induced and `final library 0` |
| R11 | Frozen held-out | `run --condition C --freeze --skills runs/stubC/library.json --provider stub --seeds 101-120` | 0 induction calls, library size 2 in every row |
| R12 | Macro ablation | `run --condition E --provider stub --seeds 1-20`, then with `--admission valid` | `tested`: 0 macros admitted (they fail the room tests); `valid`: some admitted |
| R13 | First live induction | `.env` with key, `run --condition C --tier 1 --seeds 1-5 --max-calls 300` | `induction.jsonl`: real model replies, feedback rounds; check whether any skill is accepted and what it looks like (`skills --test --file <out>/library.json`) |
