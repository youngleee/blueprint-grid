# Project Plan: Action Space Expansion in an LLM Dungeon Agent

Master's semester project (~15 weeks). Status: **draft for supervisor review**.

## Implementation status

Update this table in the same commit as the work it describes.

| Week | Milestone | State |
|---|---|---|
| 1 | Supervisor sign-off on §3, §16; model pinned | open |
| 2 | World core: Rng, map generator, visibility, turn-based world | done |
| 3 | Guard, door, items, all primitives, solvable scenario tiers | done |
| — | CLI `play` / `render` + `docs/TESTING.md` (manual testing aid) | done |
| 4 | Facts, condition evaluator, `go_to` / `explore` | done |
| 4b | MARS host for `DungeonWorld`, PyGame update | deferred to week 13 (demo only; experiment does not need MARS) |
| 5 | Action space, LLM client + cache + budget, `choose_action`, conditions A and R, experiment runner | done (offline); live calibration needs API key |
| 6–8 | Skill DSL, interpreter, validator, scenario tester, induction | — |
| 9–11 | Persistent library, held-out evaluation, ablations E / B | — |
| 12–15 | Statistics, plots, report | — |

---

## 1. Motivation and problem statement

The previous prototype (`AdaptiveAgent`) let an LLM propose detours made of
primitive moves and stored them as `CompositeAction`s. Review with the
supervisor concluded that this does **not** demonstrate action space expansion:

- a stored skill was a fixed sequence of moves (a macro / cached plan),
- it was reused only because the second wall had the same shape,
- the world was fully observable and deterministic, so classical search (A*)
  solves the task better,
- skills carried no semantics (no parameters, preconditions, effects).

This project replaces it with a setting in which expansion is necessary and
measurable: a partially observable, stochastic dungeon where the agent must
invent reusable, parameterized, closed-loop skills.

## 2. Research question and claim

**RQ:** Can an LLM agent extend its own action space with verified skills such
that it solves unseen, stochastic tasks it cannot solve with primitives alone
under the same decision budget?

**Claim to demonstrate:**

> An LLM agent in a partially observable, stochastic grid dungeon grows its
> action set with verified, parameterized, closed-loop skills, and with the same
> decision budget solves held-out dungeons that a primitives-only agent cannot.

**Sub-questions**

1. Does a persistent skill library raise held-out success under a fixed
   decision budget? (C vs. A)
2. Is closed-loop, parameterized structure required, or would macros suffice?
   (C vs. E)
3. Does LLM usage per episode decrease as the library grows? (C over episodes)

## 3. Definition: action space expansion

The agent's action set at decision step *t* is

```
A_t = Primitives ∪ Library_t
```

A skill *s* added to `Library_t` counts as an expansion iff it is:

| # | Property | Operational check |
|---|---|---|
| 1 | **Directly selectable** | The policy chooses *s* as one decision, same as a primitive |
| 2 | **Closed-loop** | Body contains at least one observation-dependent branch or loop with termination condition (an *option* in the sense of Sutton, Precup & Singh 1999) |
| 3 | **Parameterized** | Binds to typed entities/items at call time, not absolute coordinates |
| 4 | **Declared contract** | Has precondition and expected effect, both checked at runtime |
| 5 | **Verified** | Passes generated test scenarios before registration (§6.4) |
| 6 | **Capability-raising** | Increases success on held-out tasks under the same decision budget |

Property 6 answers the objection "the agent could already do this with
primitives": physical capability is unchanged by construction; capability is
measured *relative to a decision budget* (temporal abstraction shortens the
decision horizon — standard in hierarchical RL).

## 4. Related work (reading list, week 1)

| Work | Relevance |
|---|---|
| Sutton, Precup, Singh (1999), *Between MDPs and semi-MDPs* | Formal definition of options (initiation set, policy, termination) |
| Wang et al. (2023), **Voyager**, arXiv:2305.16291 | LLM-written executable skill library, reused in new worlds |
| Zhao et al. (2024), **LearnAct**, arXiv:2402.15809 | Explicit framing: LLM agents expand their own (otherwise fixed) action space |
| Wang et al. (2025), **ASI – Inducing Programmatic Skills**, arXiv:2504.06821 | Programmatic skills with verification beat text skills |
| Yu et al. (2025), **PolySkill**, arXiv:2510.15863 | Decouple skill goal from implementation → transfer |
| Song & Wei (2026), **Skill Shadowing**, arXiv:2605.24050 | Larger libraries can degrade selection — risk to measure |
| Samvelyan et al. (2021), **MiniHack**, arXiv:2109.13202 | Precedent for grid dungeons as agent testbeds |
| Yao et al. (2023), **ReAct** | Baseline step-wise LLM decision loop |
| Fikes & Nilsson (1971), **STRIPS** | Precondition/effect representation |

## 5. Environment specification

### 5.1 Architecture decision: pure C# world core, MARS as host

The world logic lives in a **plain, deterministic, cloneable C# model**
(`DungeonWorld`). MARS hosts it for tick scheduling, CSV output and
visualization; the experiment runner drives the same core headlessly.

*Why:* skill verification (§6.4) must run many short simulations in generated
micro-scenarios. That requires cheap construction and exact reproducibility,
which is awkward inside a MARS simulation run. Keeping MARS as the host keeps
the project in the MARS ecosystem for the demo.

### 5.2 Randomness

- SplitMix64 (`Dungeon/Core/Rng.cs`) with **copyable state**;
  `System.Random` cannot be cloned or reliably reseeded mid-run.
- Separate streams per purpose, derived from the seed with a stable hash:
  `layout` (map), `entity` (placement and traits), `outcome` (action results
  during the episode). Changing entity logic does then not change the layout
  for the same seed. A unit test pins the first `layout` value for seed 1.

### 5.3 Map

- Procedural: 4–6 rooms (5–10 cells per side) on a 40×40 grid, connected as a
  chain by L-shaped corridors. Start is the centre of the first room, exit the
  centre of the last.
- **Sight radius r = 3** (Chebyshev). Walls and closed doors block line of sight
  (Bresenham); they are themselves visible. The agent keeps a memory of seen
  cells (fog of war).
- Solvability is guaranteed by construction (see *Placement* below) and checked
  by property tests over 1 000 seeds per tier.

### 5.4 Entities (MUST: Guard, Door; SHOULD: Trap)

All probabilities are per-entity traits drawn from the ranges below with the
`entity` stream, so held-out seeds behave differently; outcomes are rolled with
the `outcome` stream.

**Guard**

Stationary guard on a corridor chokepoint. Talk/give/throw only work while the
guard is `idle` or `alert`.

| State | Transition | Probability (range) |
|---|---|---|
| `idle` | agent within 2 cells with line of sight → `alert`, patience reset | 1.0 |
| `alert` | agent leaves detection range → `idle`, patience reset | 1.0 |
| `alert` | `talk` (range 2) → `calm` (permanent, lets agent pass) | p_calm ∈ [0.3, 0.7] |
| `alert` | `give(food)` (adjacent) → `distracted(k)` | p_food ∈ [0.5, 0.9] |
| `alert` | `give(coin)` (adjacent) → `bribed` (permanent) | p_coin ∈ [0.2, 0.8] |
| `idle`/`alert` | `throw(item, cell)` within 4 of guard → `distracted(k)` | p_throw ∈ [0.6, 1.0] |
| `alert` | patience runs out (P ∈ [3, 5] ticks in range, not persuaded) → `hostile` → **episode fails** | 1.0 |
| `distracted(k)` | after k ticks → `alert` (if agent in range) or `idle` | k ∈ [3, 6] |

`idle`, `alert`, `hostile` block movement; `calm`, `bribed`, `distracted` do not.
Failed interactions cost a tick and patience, which is what makes open-loop
retrying risky.

**Door**

| Type | Interaction | Probability (range) |
|---|---|---|
| `locked` | `use(key, door)` with matching key → `open` | 1.0 |
| `locked` | `use(lockpick, door)` → `open` | p_pick ∈ [0.2, 0.5] per attempt |
| `jammed` | `use(none, door)` (push) → `open` | p_push ∈ [0.3, 0.6] per attempt |

Closed doors block movement and sight.

**Placement (implemented in `ScenarioGenerator`):** obstacles go on corridor
cells whose removal disconnects start from exit (chokepoints), spread along the
room chain. Each obstacle's required item (key for its locked door, food for a
guard) is placed in a room reachable while that obstacle and all later ones are
closed — solvable by construction. Extra items (coin, lockpick, food, tool) are
alternatives and distractors. Tiers: 1 = one guard, 2 = locked + jammed door,
3 = all three.

**Trap (SHOULD, not implemented)** — hidden; `look` reveals it within radius 1 with p_detect ∈
[0.4, 0.8]; `use(tool, trap)` disarms; stepping on it fails the episode.

**Items:** `key` (opens one door), `lockpick`, `food`, `coin`, `tool` — placed
in rooms, picked up with `pick_up`. `tool` is a distractor until traps exist.

### 5.5 Primitives (trusted, hard-coded in `DungeonWorld`)

| Primitive | Arguments | Notes |
|---|---|---|
| `move` | `dir ∈ {N,E,S,W}` | `failed` into walls, closed doors, blocking guards |
| `wait` | — | |
| `look` | — | currently only spends a tick; will reveal traps |
| `pick_up` | — | picks the item on the agent's cell |
| `talk` | `guard` | within 2 cells, line of sight |
| `give` | `item, guard` | guard adjacent (Chebyshev 1); food → distracted, coin → bribed |
| `throw` | `item, cell` | cell within 3, floor, line of sight; distracts guards within 4 |
| `use` | `item \| none, door` | door orthogonally adjacent; `none` = push |

Every action returns `success`, `failed` (a tick passed, no effect) or
`invalid` (impossible now, **no tick**). The episode ends `succeeded` on the
exit cell or `failed` when a guard turns hostile; afterwards every action is
`invalid`. A valid primitive costs 1 tick; a primitive **chosen by the LLM**
costs 1 decision.

### 5.6 Observation → facts

Each decision the agent receives a fact list built only from what it has seen
(`Dungeon/Agent/FactSet.cs`), never the raw map. Example (seed 1, tier 3,
after one `explore`):

```
tick 27, position (14,18), exit not found yet
inventory: key_1 (key, opens door_2)
in sight:
  door_1 jammed door at (11,18), closed, distance 3
remembered (out of sight):
  food_2 food on floor at (16,22), distance 4, last seen tick 25
unexplored frontier: 4 cells, nearest reachable at distance 2
last action: explore -> interrupted after 9 ticks: spotted door_1
```

- **Agent memory** (`AgentMemory`) stores the last-known state of every entity
  seen. It is *not* updated when an entity changes out of sight, so beliefs can
  be stale (e.g. a guard remembered `alert` that has since calmed down).
- All agent-side code goes through `AgentContext`: `Do` executes a primitive
  and updates memory; `Decide` wraps one whole decision and records its outcome
  as `last action`.
- A property test over 1 000 seeds checks the facts never mention an entity the
  agent has not seen.

**Condition language** (`Dungeon/Agent/Condition.cs`), used for skill
preconditions, effects and branches, and evaluated against a `FactSet`:

| Predicate | Type | Meaning |
|---|---|---|
| `visible(x)` | bool | entity or `exit` in sight now |
| `known(x)` | bool | seen at some point (or held) |
| `adjacent(x)` | bool | orthogonally adjacent |
| `has(x)` | bool | holding item id `x` or any item of type `x` |
| `at_exit()` | bool | standing on the exit |
| `state(x)` | text | last-known state: guard `idle/alert/calm/distracted/bribed/hostile`, door `open/closed`, item `held/on_floor` |
| `type(x)` | text | `guard`, `locked`, `jammed`, `key`, `food`, … |
| `dist(x)` | int | Chebyshev distance to last-known position |
| `count(t)` | int | held items of type or id `t` |

Combine with `and`, `or`, `not`, parentheses or `and(...)`/`or(...)`/`not(...)`;
compare with `== != < <= > >=` and `in [a, b]`. Parameter names (e.g. `g`) are
resolved through bindings at evaluation time. Facts about unknown entities make
a condition false, never an error. Malformed conditions are rejected at parse
time with a message the induction loop can feed back to the LLM.

*Why facts:* keeps prompts small (cheap model), makes preconditions checkable
by code, and stops the LLM from doing pathfinding — navigation is handled by
the built-in helpers (§6.2), so the research question is about interaction
skills, not path planning.

## 6. Agent design

### 6.1 Decision loop (ReAct-style)

```
loop until goal | failure | decision budget exhausted:
    facts      = Observe(world)
    applicable = { a ∈ A_t | a.pre(facts) holds }
    choice     = LLM.Choose(goal, facts, applicable, recent_history)   // 1 decision
    result     = Execute(choice)                                       // n ticks
    if result is failure or no progress for N decisions:
        maybe Induce()                                                 // §6.3
```

**Implemented (week 5)** without the `applicable` filter and `Induce` step,
which arrive with skills:

- `ActionSpace` (`Dungeon/Agent/ActionSpace.cs`) is `A_t`: a registry of named
  actions with typed parameters. It starts with the 8 primitives and 2 helpers
  for every condition; skills will be registered into it. **Every** decision
  (CLI or LLM) goes through `ActionSpace.Execute`, which validates arguments
  and records the outcome as `last action`.
- Entity arguments are validated against the agent's *memory*: naming an
  unseen entity gives the same `invalid` message as naming a non-existent one,
  so the error text cannot leak hidden state.
- `EpisodeRunner` loops facts → `IDecider` → `ActionSpace.Execute`. Stops on
  exit, hostile guard, decision budget or 500 ticks. Unparseable replies and
  invalid actions cost a decision (no tick) and appear in the next prompt's
  history, so the model sees its mistakes.
- Deciders: `LlmDecider` (choose_action prompt) and `RuleBasedDecider`
  (condition R, §9.1).

### 6.2 Built-in helpers (not counted as learned skills)

Implemented in `Dungeon/Agent/Navigation.cs`. Both plan by BFS (uniform step
cost, so shortest) over cells the agent *believes* passable: known floor, not a
remembered closed door, not a remembered blocking guard. They re-plan every
step.

- `go_to(id | exit, [distance])`: walk until within Manhattan `distance` of the
  last-known position. Default 1 for doors and guards (stand next to them), 0
  for items and the exit.
- `explore()`: walk to the nearest frontier cell (known passable cell next to an
  unknown cell).

Each helper returns `arrived`, `interrupted`, `unreachable` or `episode_over`
plus the ticks used. They **interrupt** and return control after a failed move,
when a new entity or the exit comes into sight, when a visible guard becomes
`alert`, or after 80 ticks. This keeps every decision-relevant moment a
decision of the LLM. One helper call costs one decision regardless of ticks.

These are given to **all conditions** so that navigation is not the variable
under test. Stated explicitly in the report. A test policy using only helpers,
primitives and agent knowledge clears 200/200 tier-2 seeds.

### 6.3 Skill induction

Triggered when (a) no applicable action makes progress for N=3 decisions, or
(b) an obstacle type is encountered that no library skill's precondition
covers.

Prompt contains: goal, current facts, recent failed attempts and their
outcomes, the DSL grammar, the list of available primitives and skills
(signatures only), and 2 hand-written example skills from a **different**
domain (to show format without leaking solutions).

Output: one skill in the JSON DSL (§7). Up to 3 attempts; each rejection
message (validator or test failure) is fed back.

### 6.4 Skill verification ("skill unit tests")

Verifying a skill in a clone of the real world would leak hidden information
(unseen rooms, future random outcomes). Instead:

1. **Static validation:** schema valid; only known actions/skills called;
   parameter types match; every `until`/`repeat` has `max ≤ 5`; nesting depth
   ≤ 3; no recursion; total primitive bound ≤ 30 ticks.
2. **Generated scenario tests:** from the skill's precondition, generate k=5
   micro-rooms (fresh seeds, randomized entity probabilities) in which the
   precondition holds. Run the skill. **Accept if the declared effect holds in
   ≥ 3/5** (threshold configurable; reported).
3. **Dedup:** reject if an existing skill has the same signature and
   structurally equal body.

Accepted skills are registered and persisted. Rejected skills are logged with
reason.

### 6.5 Runtime contract checks

- Precondition false → skill not offered.
- Effect false after execution → counted as `skill_effect_failure`; agent gets
  the outcome in its next facts.

### 6.6 Library persistence

`skills.json` with name, signature, pre, effect, body, provenance (episode,
seed, LLM call id), test pass rate, usage count, success count.

## 7. Skill DSL

JSON, interpreted in C#. LLM-generated code is never executed directly.

### 7.1 Grammar (informal)

```
Skill     := { name, params: [Param], pre: [Cond], effect: [Cond], body: [Stmt] }
Param     := { name, type ∈ {guard, door, trap, item, cell} }
Stmt      := Call | If | Until | Repeat | Fail
Call      := { call: ActionName, args: [Expr] }
If        := { if: Cond, then: [Stmt], else: [Stmt]? }
Until     := { until: Cond, max: int, do: [Stmt] }
Repeat    := { repeat: int, do: [Stmt] }
Fail      := { fail: string }
Cond      := predicate expression over facts, e.g.
             "state(guard) == alert", "has(food)", "dist(guard) <= 2",
             "not(...)", "and(...)", "or(...)"
Expr      := param name | literal | helper ("away_from(guard, 3)", "nearest(item_type)")
```

### 7.2 Example

```json
{
  "name": "distract_guard",
  "params": [{"name": "g", "type": "guard"}, {"name": "it", "type": "item"}],
  "pre":    ["visible(g)", "has(it)", "state(g) != hostile"],
  "effect": ["state(g) in [distracted, bribed, idle]"],
  "body": [
    {"call": "go_to", "args": ["g", 2]},
    {"if": "state(g) == alert",
     "then": [{"call": "throw", "args": ["it", "away_from(g, 3)"]}],
     "else": [{"call": "talk", "args": ["g"]}]},
    {"until": "state(g) in [distracted, idle]", "max": 3, "do": [{"call": "wait"}]}
  ]
}
```

### 7.3 Macro variant (ablation E)

Same induction pipeline, but the grammar allows only `Call` statements with
literal arguments bound at creation time — no `params`, `If`, `Until`.
Contracts are kept so selection works identically.

## 8. LLM integration

### 8.1 Model

Cheap OpenAI model via the Responses API (`Dungeon/Llm/OpenAiClient.cs`).
Default **`gpt-4.1-mini-2025-04-14`** (dated snapshot, overridable with
`OPENAI_MODEL`); temperature 0; JSON output mode; 300 max output tokens.
Retries 429/5xx/network errors with exponential backoff (4 attempts); 4xx
fails immediately. Confirm the model in week 1 by checking JSON validity on a
first live run.

### 8.2 Two prompt types

| Prompt | Frequency | Output | State |
|---|---|---|---|
| `choose_action` | every decision | `{"action": name, "args": [...], "reason": "..."}` | done (`Dungeon/Agent/Prompts.cs`) |
| `induce_skill` | rare | one DSL skill | week 8 |

`choose_action` = system part (rules of the world, every action's signature
and description from `A_t`, reply format, 2 examples) + user part (decisions
left, current facts §5.6, last 6 decision outcomes). The system part only
changes when `A_t` changes. A test checks over 50 seeds that no hidden entity
parameter (chances, patience) appears in any prompt. The parser accepts minor
shape deviations (code fences, numbers as args, args as one string); names and
arguments are then validated by `ActionSpace`.

### 8.3 Record/replay cache (reproducibility)

- Key: SHA-256 of `(version, model, temperature, max_output_tokens,
  prompt_type, system, user)` (`Dungeon/Llm/CachingClient.cs`).
- Store: `cache/llm/<provider>/<hash[0:2]>/<hash>.json` with request,
  response, token usage and time; written atomically.
- Modes: `live` (always call, overwrite), `record` (call on miss; default),
  `replay` (read only, fail on miss).
- A seeded run in `replay` mode is bit-for-bit reproducible without network
  (tested: metrics rows identical except the cache-hit column).
  Live variance is measured separately (§9.5).

### 8.4 Cost control

- Hard caps per run on **real** calls and tokens (`MeteredClient`, defaults
  2 000 calls / 3 M tokens, CLI `--max-calls`, `--max-tokens`). It wraps the
  network client *inside* the cache, so cached answers are free and a replay
  of a capped run still works. On a cap or API error the runner stops, keeps
  finished episodes and writes `run.json` with the reason.
- Tokens per call are in every trace line and summed per episode and run.
- Offline stub provider (`--provider stub`): explores until the exit is in the
  facts, then walks there. For pipeline checks only; never a result.
- **Calibration (needs key):** first live run of A on tier 3 gives tokens per
  decision and cost per episode; adjust episode counts before the full
  experiment. Offline estimate from the stub: ≈ 700 input + 15 output tokens
  per decision.

## 9. Experimental design

### 9.1 Conditions

| ID | Name | Induction | Library persists | Skill form | State |
|---|---|---|---|---|---|
| **A** | Primitives only | no | — | — | implemented |
| **R** | Hand-written rules, no LLM | no | — | — | implemented |
| **B** | Induction, reset each episode | yes | no | DSL | — |
| **C** | Induction, persistent library | yes | yes | DSL | — |
| **E** | Macro ablation | yes | yes | macro (§7.3) | — |
| D (could) | Hand-written library | no | fixed | DSL | — |

Must: A, C, E. Should: B. Could: D.

**R** (added in week 5) is a fixed if-then policy over the same facts and the
same action set (`RuleBasedDecider`): go to the exit if known; bribe/feed/talk
to a visible guard; pick up items; explore; open doors with key, lockpick or
push. It answers the reviewer question "would simple rules do?" and is
reported alongside A.

First offline numbers for R, seeds 1–50, D_max = 40:

| Tier | Success | Mean decisions | Failures |
|---|---|---|---|
| 1 (guard) | 86 % | 11.6 | all `caught by guard_1` |
| 2 (doors) | 100 % | 19.8 | — |
| 3 (all) | 86 % | 26.8 | 5 caught, 2 decision budget |

R's guard failures are the open-loop pattern the project targets: it repeats
`give(coin, guard)` after refusals until patience runs out. A closed-loop skill
that switches strategy on refusal should avoid exactly this. Consequence for
the claim: **C must be compared with R, not only with A**; if C does not beat
R, that is reported.

### 9.2 Seeds and phases

- **Training:** 20 seeds, curriculum in 3 tiers (guard only → door only →
  guard+door [→ +trap]).
- **Held-out test:** 20 seeds never seen in training, entity probabilities drawn
  from the same ranges, different layouts.
- **Test-time setting:** library frozen; reported twice — induction disabled
  (pure transfer) and induction enabled.

### 9.3 Budget

- Decision budget per episode: **D_max** (calibrated in week 5 so that A
  succeeds on ≤ ~40 % of tier-3 tasks — tuned openly, value reported). CLI
  default 40 until calibrated with live runs.
- Tick budget per episode: T_max = 500 (safety cap).
- Induction calls do not count as decisions but are capped (≤ 3 per episode)
  and reported separately.

### 9.4 Metrics (per episode, CSV)

Implemented columns of `metrics.csv` (`EpisodeResult`): `condition, tier,
seed, success, end_reason, decisions, ticks, invalid_actions, parse_errors,
llm_calls, cache_hits, tokens_in, tokens_out, action_set_size`. Each run
directory also has `run.json` (configuration, decider, model, cache mode,
budget, summary; never the API key) and `traces/seed_N.jsonl` (per decision:
facts, action, args, reason, outcome, raw reply, tokens).

Planned columns, added with skills:

| Metric | Purpose |
|---|---|
| `success` | primary |
| `decisions_used`, `ticks_used` | efficiency |
| `llm_calls_choose`, `llm_calls_induce`, `tokens_in/out` | cost; should fall over episodes in C |
| `library_size`, `max_skill_depth` | expansion over time |
| `skills_induced`, `skills_rejected` (+ reason) | induction quality |
| `skill_invocations`, `skill_reuse_new_seed` | reuse / transfer |
| `skill_pre_fail`, `skill_effect_fail` | robustness under stochasticity |
| `wrong_skill_selected` | skill shadowing indicator |
| `failure_reason` | hostile / trap / budget / stuck |

### 9.5 Statistics

- Held-out success rate per condition with Wilson 95 % CI.
- Paired comparison per seed (C vs. A, C vs. E): McNemar test.
- Live variance: repeat condition C held-out 3× in `live` mode; report spread.

### 9.6 Planned results

1. **Expansion curve:** `library_size` and `llm_calls/episode` vs. training
   episode (condition C, B for contrast).
2. **Main result:** held-out success for A / B / C / E under equal D_max.
3. **Qualitative trace:** one skill's life cycle — induced, tested (k/5),
   reused on an unseen seed where the entity behaved differently, with a branch
   taken that was not taken during induction.

### 9.7 Hypotheses

- H1: C > A on held-out success (equal D_max).
- H2: C > E on held-out success; gap larger on seeds with high outcome variance.
- H3: In C, LLM calls per episode decrease across training episodes.
- H4: C ≥ R on held-out tier-3 success (equal D_max).

A negative result for any hypothesis is reported as such.

## 10. Software architecture

Files marked ✓ exist; the rest are planned.

```
GridBlueprint/            (old prototype, MARS host; to be replaced by the dungeon host)
Dungeon/                  class library, no MARS dependency
  Core/Rng.cs             ✓ copyable PRNG, named streams
  Core/Geometry.cs        ✓ Cell, Direction, Room
  Core/DungeonMap.cs      ✓ immutable layout
  Core/Generator.cs       ✓ seeded rooms + corridors
  Core/ScenarioGenerator.cs ✓ chokepoints, obstacle + item placement, tiers
  Core/Scenario.cs        ✓ map + initial entities
  Core/Visibility.cs      ✓ sight radius, line of sight
  Core/DungeonWorld.cs    ✓ episode state, primitives, entity updates, Clone()
  Core/ActionResult.cs    ✓ success / failed / invalid
  Entities/Guard.cs, Door.cs, Item.cs ✓   Trap.cs (should)
  Render/AsciiRenderer.cs ✓ agent view, reveal view, status, debug
  Render/CommandInterpreter.cs ✓ text commands → primitives (CLI, later LLM output)
  Agent/AgentMemory.cs    ✓ last-known entity states, exit, last outcome
  Agent/AgentContext.cs   ✓ world + memory; Do (primitive) / Decide (decision)
  Agent/FactSet.cs        ✓ observation → facts + prompt text
  Agent/Condition.cs      ✓ condition parser / evaluator
  Agent/Navigation.cs     ✓ go_to, explore (BFS on believed-passable cells)
  Agent/ActionSpace.cs    ✓ A_t registry, argument validation, standard actions
  Agent/Prompts.cs        ✓ choose_action prompt
  Agent/DecisionParser.cs ✓ model reply → Decision
  Agent/Deciders.cs       ✓ LlmDecider, RuleBasedDecider (R)
  Agent/EpisodeRunner.cs  ✓ decision loop, traces, EpisodeResult
  Agent/ExperimentRunner.cs ✓ seeds × condition → run.json, metrics.csv, traces
  Llm/OpenAiClient.cs     ✓ Responses API, retries
  Llm/CachingClient.cs    ✓ record / replay cache
  Llm/MeteredClient.cs    ✓ call / token caps
  Llm/StubLlmClient.cs    ✓ offline pipeline stub
  Llm/LlmSettings.cs      ✓ environment / .env settings
  Skills/Skill.cs         model + JSON (de)serialization
  Skills/Interpreter.cs   executes DSL, returns ticks + outcome
  Skills/Validator.cs     static checks
  Skills/ScenarioTester.cs generated micro-room tests
  Skills/Library.cs       registry + persistence
  Llm/Prompts (induce_skill)
  Agent/LlmAgent: induction trigger
Dungeon.Cli/              ✓ play / render / run
Dungeon.Tests/            ✓ xUnit
Visualization/            PyGame (to be extended: fog of war, entities, skill log)
docs/PLAN.md, docs/TESTING.md ✓
```

**Reused from the old prototype:** MARS host setup, `ILlmClient` and the OpenAI
client (to be extended with caching), `.env` loading, logging style,
visualization skeleton, the "LLM proposes, code decides" validator principle.

**Retired:** `AdaptiveAgent`, `CompositeAction`, `ManualSkills`,
`FakeLlmClient` canned detour, `BenchmarkRunner`. Kept on a tagged commit
`v0-detour-prototype` for reference.

## 11. Testing strategy

| Layer | What | Kind |
|---|---|---|
| Rng | same seed → same sequence; clone continues identically; pinned value | unit |
| Generator | 1 000 seeds: connected, borders closed, no overlap, deterministic | property |
| ScenarioGenerator | 1 000 seeds × 3 tiers: obstacles are chokepoints, required items reachable before them, no shared cells; scripted solver clears 200 tier-2 seeds | property |
| Entities | each transition table row, incl. probability edges 0 / 1 | unit |
| Visibility | walls and closed doors block sight; radius boundary | unit |
| Renderer / commands | agent view leaks nothing hidden; inspect commands are free; bad input costs no tick | unit |
| Memory / facts | only seen entities; stale beliefs kept; 1 000-seed no-leak property | unit, property |
| Conditions | every predicate, combinators, bindings, each parse error | unit |
| Navigation | shortest path, no planning through unknown / closed doors, interrupts; knowledge-only policy clears 200 tier-2 seeds | unit, integration |
| Action space | registry, argument validation against memory (unseen = non-existent), outcome recording | unit |
| LLM client | request body, output parsing, retry / no-retry rules (stubbed HTTP, no network) | unit |
| Cache / budget | record, replay, live, miss error, key covers every field; caps; cache hits free | unit |
| Prompt / parser | lists every action, no hidden parameters over 50 seeds, history bound; reply shapes | unit, property |
| Runner | budgets, invalid / unparseable handling, hostile end, outputs; **replay reproduces a recorded run exactly** | integration |
| Interpreter (planned) | each statement type; `max` bound enforced; failure propagation | unit |
| Validator (planned) | each rejection rule has a failing fixture | unit |
| ScenarioTester (planned) | known-good skill passes; known-bad skill fails | unit |

No test touches the network. The LLM path is exercised with scripted clients
(fixed replies), the stub provider and a stubbed HTTP handler; these are never
reported as results.

## 12. Timeline and milestones

| Week | Work | Acceptance criterion |
|---|---|---|
| 1 | Reading; finalize definition (§3), conditions, model choice; supervisor sign-off | Plan approved; model snapshot pinned |
| 2 | Rng, DungeonWorld, Generator, Visibility | 1 000-seed generator property test green |
| 3 | Guard, Door, items, primitives | Entity transition tests green |
| 4 | Facts, helpers (`go_to`, `explore`), MARS host, visualization update | Manual walk-through of a seed in PyGame with fog of war |
| 5 | LLM client + cache, `choose_action`, condition A end to end; **calibrate D_max**; cost estimate | A runs 20 seeds; replay matches; cost/episode known |
| 6 | Skill model, DSL parser, interpreter | Interpreter tests green; hand-written `distract_guard` works |
| 7 | Validator, ScenarioTester | Known-good passes, known-bad fails |
| 8 | **Checkpoint.** Induction prompt + loop | One skill induced, verified and reused live. *If not reached: cut traps and nesting.* |
| 9 | Library persistence, condition C, curriculum | C completes 20 training seeds |
| 10 | Held-out evaluation C vs. A | First main-result numbers |
| 11 | Macro ablation E; condition B if time | E and B results |
| 12 | Live-variance repeats; statistics | CIs and McNemar computed |
| 13 | Plots, qualitative trace, demo config | Figures 1–3 final |
| 14 | Report writing | Draft to supervisor |
| 15 | Revisions, presentation, buffer | Final submission |

## 13. Scope (MoSCoW)

| Must | Should | Could | Won't (this semester) |
|---|---|---|---|
| Guard + Door, sight radius, seeded generator | Trap | Hand-written library (D) | Fine-tuning / RL training |
| Facts observation, primitives, helpers | Skill nesting (depth > 1) | Skill refinement after failures | Multiple agents |
| DSL + interpreter + validator + scenario tests | Condition B | Local open-weight model comparison | Natural-language dialogue with guards |
| LLM record/replay cache | Live-variance repeats | Skill-shadowing analysis | Generated C# code execution |
| Conditions A, C, E; held-out evaluation | PyGame library-growth view | Automatic curriculum | |

## 14. Risks and mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Cheap model produces invalid DSL | high | medium | JSON mode, grammar in prompt, 2 format examples, feedback loop (3 attempts), validator messages specific |
| Induced skills are trivial macros anyway | medium | high | Closed-loop is a validator requirement for condition C (§3 property 2); E quantifies the difference |
| A already solves everything | medium | high | Calibrate D_max in week 5; stochastic entities punish open-loop play |
| Simple rules (R) already solve everything | medium | high | Offline R is 86 % on tier 3; its failures are open-loop guard retries. Report C vs. R honestly (H4); if needed, widen guard trait ranges and report the change |
| Environment work overruns | high | high | Guard + Door only until week 10; checkpoint at week 8 |
| API cost overrun | low | medium | Hard caps, cache, cost estimate in week 5 |
| Non-reproducible results | medium | high | Record/replay cache; pinned snapshot; seeds in every CSV row |
| Information leakage via verification | medium | medium | Verify in generated micro-rooms, never in a clone of the real episode |
| MARS integration friction | medium | low | World core independent of MARS; MARS only hosts the demo |

## 15. Deliverables

1. Source code (this repository), tagged release.
2. `skills.json` libraries from the final runs + full LLM cache (replayable).
3. Metrics CSVs and plotting notebook/script.
4. Report: motivation, definition, environment, method, results, limitations.
5. Demo: PyGame run of a held-out seed showing a learned skill in use.

## 16. Open questions for the supervisor

1. Is the budget-relative definition of expansion (§3, property 6) accepted?
2. Are the built-in navigation helpers (`go_to`, `explore`) acceptable, given
   the focus on interaction skills rather than path planning?
3. Verification threshold: ≥ 3/5 scenario tests — acceptable, or stricter?
4. Report language (German / English) and expected length.
5. Is an API budget available, or should the cost cap be planned around
   personal funds?
6. Is a hand-written rule baseline (R, §9.1) the right "classical" comparison,
   or is a different baseline expected?
