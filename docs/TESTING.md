# Testing the Dungeon Environment

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

## Manual tools

```bash
# Full dungeon incl. hidden entities and their chances (debug view)
dotnet run --project Dungeon.Cli -- render --seed 1 --tier 3

# Play as the agent (only what the agent can see is shown)
dotnet run --project Dungeon.Cli -- play --seed 1 --tier 3 [--sight 3]

# Scripted run: pipe commands, output is reproducible for the same seed
printf 'state\nw\nw\nuse none door_1\n' | dotnet run --project Dungeon.Cli -- play --seed 1 --tier 3
```

Tiers: `1` one guard · `2` locked + jammed door · `3` all three.
In `play`, type `help` for commands. Command groups:

- **Primitives** (`move`, `wait`, `look`, `pick_up`, `talk`, `give`, `throw`,
  `use`): one tick each, none if `invalid`.
- **Helpers** (`goto <id|exit> [distance]`, `explore`): several ticks; they stop
  early when something new comes into sight, a move fails, or a guard turns
  alert.
- **Inspect** (free, no tick): `facts` (exactly what the LLM will see),
  `check <condition>` (evaluate a condition against the facts), `map`, `state`,
  `reveal`, `debug`. `reveal` and `debug` show hidden information — the agent
  never gets these.

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
| 19 | goto through unknown | `goto exit` before seeing it | `unreachable`, tick unchanged |
| 20 | Last action reports the helper | `explore`, then `facts` | `last action: explore -> …`, not a single move |
| 21 | Conditions | `check has(key) and state(door_2) == closed`, `check state(guard_1)` | `true`/`false`; second is an error (needs a comparison) |

Probabilistic cases (7, 10–12) depend on the seed: the same seed and the same
command sequence always give the same outcome; a different seed may not.
