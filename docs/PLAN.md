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
| 4b | MARS host for `DungeonWorld`, PyGame update | done (week 9): `Dungeon.Mars` replays a recorded episode in MARS; `Visualization/main.py dungeon` |
| 5 | Action space, LLM client + cache + budget, `choose_action`, conditions A and R, experiment runner | done (offline); live calibration needs API key |
| 6 | Skill DSL, interpreter, contracts, skills in `A_t`, hand-written library, conditions D and RD | done; first evidence in §9.1 |
| 7 | Skill validator, scenario tester (calibrated), admission feedback, `skills --test`, `run --skills` | done |
| 8 | `induce_skill` prompt, induction loop with feedback, conditions B / C / E, frozen held-out runs, usable-skills hint | done (offline with stub); live runs need API key |
| 9 | Experiment protocol script, analysis script (tables, McNemar, curves), `docs/EXPERIMENTS.md` | done; full protocol dry-run with the stub |
| — | Robustness audit (§14.1): 12 defects found by probing and fuzzing, all fixed with regression tests | done |
| 10–12 | Live runs: calibrate, train, held-out test, variance (on the experiment machine) | next — needs API key |
| 13–15 | Report from `results.md`, qualitative traces | — |

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
(`DungeonWorld`). The experiment runner drives it headlessly. MARS hosts a
**replay** of a recorded episode for the demo (`Dungeon.Mars`): a
`RasterLayer` is redrawn from the replayed world each tick and a MARS agent
follows the dungeon agent, so the existing PyGame websocket visualizer shows
it unchanged. Replay instead of live control keeps the demo free of LLM calls
and identical to what the experiment recorded.

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
  by property tests over 1 000 seeds per tier. A seed whose layout has too few
  chokepoints for its tier gets an alternative layout (streams `layout#1`,
  `entity#1`, …); this affects 8 of 20 000 tier-3 seeds and no tier-1/2 seed.

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
| `has_key(d)` | bool | holding the key that opens door `d` |

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

**Implemented (weeks 5–6)** without the `applicable` filter and `Induce` step:

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
  (conditions R and RD, §9.1).
- Skills are registered into `ActionSpace` as `ActionKind.Skill` and called
  through `ActionSpace.Invoke` from inside other skills (not a decision of its
  own); only the outer call counts as a decision.

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
- `retreat(id, distance)` (added week 6): walk over known cells until at least
  `distance` (Chebyshev) from the entity's last-known position. Stepping out
  of a guard's detection range resets it to `idle` with full patience (§5.4),
  so this is how a closed-loop skill recovers from a refusal.

Each helper returns `arrived`, `interrupted`, `unreachable` or `episode_over`
plus the ticks used. `go_to` and `explore` **interrupt** and return control after a failed move,
when a new entity or the exit comes into sight, when a visible guard becomes
`alert`, or after 80 ticks. This keeps every decision-relevant moment a
decision of the LLM. One helper call costs one decision regardless of ticks.

These are given to **all conditions** so that navigation is not the variable
under test. Stated explicitly in the report. A test policy using only helpers,
primitives and agent knowledge clears 200/200 tier-2 seeds.

### 6.3 Skill induction

Implemented in week 8: `Dungeon/Skills/SkillInducer.cs` (`SkillInducer`,
`InductionPolicy`) and `InducePrompts.cs`. Induction runs **before** a
decision, so a new skill can be chosen immediately.

**Triggers** (checked before every decision, B / C / E only):

- *Uncovered obstacle:* a blocking guard in sight (`idle` / `alert`) or a
  known closed door, and **no library skill is usable on it now** (a skill
  with one parameter of that kind whose precondition holds for it; for macros,
  one that names it). "Usable now" rather than "exists for the kind" is
  deliberate: a door skill that needs a key does not cover a locked door
  without one, so the agent may learn a lockpick variant.
- *Stalled:* the last 3 decisions all `failed` or were `invalid` while an
  obstacle is present.

Each obstacle triggers at most one "uncovered" and one "stalled" request per
episode. A request gets up to 3 attempts (`--induce-attempts`); an episode at
most 6 induction calls (`--induce-calls`). Induction calls **do not count as
decisions** (§9.3) but are counted and billed separately.

**Prompt** (`induce_skill`, max 1 500 output tokens): system part = world
rules, every action of `A_t` with its description, the skill language (§7.1)
and its rules, how admission works (20 test rooms, ≥ 16 passes, never caught),
and example skills for **other tasks** (fetch an item, find the exit) so the
format is shown without giving away a guard or door solution (tested). User
part = trigger, target obstacle, current facts, last 6 decisions, the current
library (signatures and contracts), and every previous attempt with its full
admission feedback.

**Admission:** each reply is parsed (§7.1), validated and scenario-tested
(§6.4) against the current `A_t` and library. The first accepted skill is
registered into `A_t` and the library at once. In C and E the library
persists to the next episode; in B it is discarded.

**Usable-skills hint:** with a non-empty library, the `choose_action` user
prompt adds `skills usable now: calm_guard(guard_1), …` — every skill call
whose precondition holds and whose effect does not hold yet. It is the
applicable-action filter of §6.1 as a hint (all actions remain callable).
Calling a skill whose effect already holds is `invalid` (`effect already
holds`) instead of a silent no-op.

**Pipeline check with the offline stub** (not a result; the stub answers
with fixed skills): tier 3, seeds 1–20, D_max 40 — A 0 %, B 95 %, C 95 %,
E 0 %; C learned 2 skills in 20 induction calls, B re-learned 40 in 55; C's
frozen library on held-out seeds 101–120: 100 % with 0 induction calls. E
learned nothing: its stub macros reach only 4–14 of 20 test rooms, which is
itself a hint of the macro-vs-skill gap the real ablation measures.

### 6.4 Skill verification ("skill unit tests")

Verifying a skill in a clone of the real world would leak hidden information
(unseen rooms, future random outcomes). Instead (implemented in week 7,
`Dungeon/Skills/SkillValidator.cs` and `ScenarioTester.cs`; `SkillAdmission`
runs both):

1. **Static validation** (`SkillValidator`, reports *all* errors at once with
   locations):
   - every call names an action of `A_t` (no self-calls), with the right
     argument count (`pos(x)` counts as two) and argument types;
   - every parameter is declared and used; no fact word as parameter name;
   - no specific entity ids (`guard_1`) — use a parameter so the skill
     generalises;
   - at least one effect condition;
   - loops `max`/`repeat` ≤ 6, nesting ≤ 5 levels, worst-case calls ≤ 40;
   - **closed-loop rule:** at least one `if`/`until` whose condition reads
     facts (`if: "true"` does not count). A fixed sequence is rejected as a
     macro. For ablation E the inverse *macro form* applies (§7.3);
   - not a renamed copy of an existing skill (same params, contract, body).
2. **Scenario tests** (`ScenarioTester`): generated micro-rooms — start room,
   5-cell corridor, exit room — one entity per skill parameter at the corridor
   entrance; entity traits drawn from the same ranges as real dungeons; the
   agent holds a random subset of food / coin / lockpick and starts in sight at
   distance 3 (80 %), 2 (13 %) or 1 (7 %), matching first sightings in real
   tier-3 episodes. A room is used only if the precondition holds **and the
   effect does not hold yet** (otherwise the test proves nothing). Test seeds
   are fixed and identical for every skill, so pass rates are paired.
   **Accept if the effect is reached in ≥ 16 of 20 rooms and no room ends in
   a lost episode.**
3. **Feedback:** on rejection, `AdmissionResult.Feedback()` lists either the
   validator errors or every test room (setup → outcome). Week 8 feeds this
   text back to the LLM.

**Calibration** (the threshold is not a guess). Three hand-written drafts of
`calm_guard` (`Dungeon.Tests/Fixtures/`) with known full-episode quality
(RD, tier 3, 200 seeds): v1 97.5 %, v3 95.5 %, v2 85.5 % (R: 87.5 %). Each was
tested on 40 disjoint blocks of 20 rooms:

| Rule | v1 accepted | v3 accepted | v2 (weak) accepted |
|---|---|---|---|
| pass rate ≥ 17/20 only | 36/40 | 39/40 | 19/40 |
| pass rate ≥ 16/20 only | 39/40 | 40/40 | 29/40 |
| **≥ 16/20 and no lost episode** (chosen) | 39/40 | 37/40 | **4/40** |

Pass rates barely separate the drafts (median 94 / 94 / 82 %); *losing the
episode* does — v2 lost an episode in 20/20 blocks, v1 in 0/20. Hence the
zero-loss rule. Measured error rates on these drafts: a good skill is wrongly
rejected in ~5 % of seed blocks, a weak one wrongly accepted in ~10 %. The
default test seeds accept v1 and v3 and reject v2; `ScenarioTesterTests`
pins all counts above. Testing one skill takes ~10 ms.

The tester also found a flaw in a hand-written skill: `open_door` was
"applicable" to locked doors without key or lockpick, where it can never
succeed (accepted in only 31/40 blocks). Its precondition now requires the
means (`type(d) == jammed or has_key(d) or has(lockpick)`): 40/40.

Accepted skills are registered; persistence across episodes comes with
condition C (week 9).

### 6.5 Runtime contract checks

Implemented in `Dungeon/Skills/SkillInterpreter.cs`:

- Precondition false → `invalid`, no tick, message `precondition not met: <cond>`.
  Skills are still *listed* in the prompt with their preconditions (the planned
  `applicable` filter of §6.1 is not applied yet), so the LLM can see why a
  skill is not usable.
- Body runs to the end, or stops on `fail`, an invalid step, a failed nested
  skill, the episode ending, or a limit (40 calls, 120 ticks, nesting depth 3).
- Effect checked on fresh facts afterwards → `success`, or `failed` with
  `effect not reached: <cond>`. Counted as `skill_effect_fail` (§9.4).
- The outcome lists every step the skill took (nested skills indented); it is
  in the trace (`skill_steps`) and its summary is the next `last action`.

### 6.6 Library persistence

`skills.json` with name, signature, pre, effect, body, provenance (episode,
seed, LLM call id), test pass rate, usage count, success count.

## 7. Skill DSL

JSON, interpreted in C#. LLM-generated code is never executed directly.

### 7.1 Grammar (implemented: `Dungeon/Skills/SkillJson.cs`)

```
Skill     := { name, description?, params: [Param], pre: [Cond], effect: [Cond], body: [Stmt]+ }
Param     := { name, type ∈ {guard, door, item, target, dir, int} }
Stmt      := Call | If | Until | Repeat | Fail           (exactly one key per object)
Call      := { call: ActionName, args?: [Arg] }          any action in A_t, incl. other skills
If        := { if: Cond, then: [Stmt], else?: [Stmt] }
Until     := { until: Cond, max: int ≥ 1, do: [Stmt]+ }  checks Cond before each pass
Repeat    := { repeat: int ≥ 1, do: [Stmt]+ }
Fail      := { fail: string }
Cond      := condition string, language of §5.6 (parameters resolved by binding)
Arg       := param name | literal (id, number, "none", "exit")
           | pos(x)     → two args: x and y of x's last-known position
           | held(t)    → id of a held item of type or id t
           | key_for(d) → id of the held key that opens door d
```

Names are lowercase identifiers; unknown keys, empty bodies, `max` < 1,
unknown parameter types, unknown argument functions and malformed conditions
are rejected with a path-specific message (e.g. `skill.body[2].then[0].max:
expected a whole number ≥ 1`), which week 8 feeds back to the LLM. The parser
checks *shape*; whether calls name real actions with the right argument count
is checked at run time now and by the validator in week 7.

Skills become actions of `A_t` through `SkillActions.ToAction`. The LLM is
shown signature, description, `Needs:` (pre) and `Achieves:` (effect) — never
the body.

### 7.2 Hand-written library (condition D, `Dungeon/Skills/Library/handwritten.json`)

`open_door(d)`: precondition `known(d)`, `state(d) == closed` and
`type(d) == jammed or has_key(d) or has(lockpick)`; walk next to `d`; until
open (max 6): key if held, else lockpick if locked, else push if jammed, else
`fail`.

`calm_guard(g)` — the skill that answers R's failure mode:

```json
{
  "name": "calm_guard",
  "params": [{ "name": "g", "type": "guard" }],
  "pre": ["known(g)", "state(g) != hostile"],
  "effect": ["state(g) in [calm, bribed, distracted]"],
  "body": [
    { "if": "has(food) and visible(g) and dist(g) <= 3", "then": [
      { "call": "throw", "args": ["held(food)", "pos(g)"] } ] },
    { "until": "state(g) in [calm, bribed, distracted]", "max": 3, "do": [
      { "until": "dist(g) <= 2", "max": 3, "do": [{ "call": "go_to", "args": ["g", 2] }] },
      { "call": "talk", "args": ["g"] },
      { "if": "state(g) == alert", "then": [{ "call": "retreat", "args": ["g", 3] }] } ] },
    { "if": "has(coin) and not state(g) in [calm, bribed, distracted]", "then": [
      { "until": "dist(g) <= 1", "max": 3, "do": [{ "call": "go_to", "args": ["g", 1] }] },
      { "call": "give", "args": ["held(coin)", "g"] } ] }
  ]
}
```

Design notes, each from a run, not assumed:

1. *Throw from range first.* Food thrown from ≤ 3 cells distracts without
   spending patience; giving it needs adjacency, where a refusal plus the step
   away can exhaust patience (observed: give → refuse → retreat → caught).
2. *Retreat after each refusal.* Leaving detection range resets patience; this
   is the closed loop that a fixed sequence lacks.
3. *Coin last.* Adjacent bribes are the riskiest attempt; used only when
   throwing and talking failed.

**Draft history (corrected in week 7; the week-6 notes had v1 and v2 mixed
up).** Three drafts were written, all kept as test fixtures in
`Dungeon.Tests/Fixtures/`:

| Draft | Strategy | RD tier 3 (200 seeds) | vs R (exact McNemar) |
|---|---|---|---|
| v1 | loop: walk up, coin / food / talk, retreat if still alert | 97.5 % | p = 0.0001 |
| v2 | coin once, food once (both adjacent), then talk | 85.5 % | p = 0.58 (no better) |
| v3 (library) | throw food from range, talk with retreat, coin last | 95.5 % | p = 0.005 |

v2 was written to satisfy a unit test that encoded a wrong strategy ("give
food, and on refusal switch to talking"). Lesson: unit tests check
*mechanics*, only outcomes check *quality*. v3 stays the library skill because
it is the most deliberate design and is the result reported; v1 scoring
slightly higher shows the hand-written reference is not tuned to the ceiling.

Over 200 tier-3 seeds the same skill took **25 distinct step paths**
(e.g. `throw`; `talk`; `talk > retreat > go_to > talk`), all from one
definition, because each branch depends on the guard's actual response.

### 7.3 Macro variant (ablation E)

Same induction pipeline, prompt variant and validator form
(`SkillForm.Macro`): only `call` statements, no parameters, no `if` / `until`
/ `repeat` / `fail`; specific ids (`guard_3`) are allowed and expected.
Contracts are kept so selection works identically. Macros are scenario-tested
in the same rooms as skills, with each named guard or door placed under its
own id.

Because the admission bar was calibrated on closed-loop skills, macros may
almost never pass it — which is part of the finding, but makes E learn
nothing at all. **Sensitivity variant:** `--admission valid` admits anything
the validator accepts (tests still run and are logged). Report E with both
`tested` and `valid` admission; use `valid` for C too in that comparison so
the only difference is the skill form.

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
| `induce_skill` | rare | one DSL skill | done (`Dungeon/Skills/InducePrompts.cs`, §6.3) |

`choose_action` = system part (rules of the world, every action's signature
and description from `A_t`, reply format, 2 examples) + user part (decisions
left, current facts §5.6, the usable-skills line when a library exists
(§6.3), last 6 decision outcomes). The system part only
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
- **Trace replay** (`EpisodeReplay`, CLI `replay`, protocol phase `verify`)
  is a second, LLM-free check: it re-executes every recorded decision (and
  registers learned skills when they were admitted) from the trace alone and
  requires each outcome to match. The full stub dry run (27 runs, 165
  episodes) replays exactly; an edited trace is detected.
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
| **RD** | R's rules + hand-written library | no | fixed | DSL | implemented |
| **B** | Induction, reset each episode | yes | no | DSL | — |
| **C** | Induction, persistent library | yes | yes | DSL | — |
| **E** | Macro ablation | yes | yes | macro (§7.3) | — |
| **D** | LLM + hand-written library | no | fixed | DSL | implemented |

Must: A, C, E. Should: B. D and RD are cheap references and are implemented.

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

**RD** (week 6) is R with one change: when `calm_guard` / `open_door` are in
`A_t`, the same rules call them instead of single primitives. The R–RD gap
therefore isolates what the skills add, with no LLM involved. Seeds 1–200,
D_max = 40, same seeds for both (paired):

| Tier | R | RD | only R wins | only RD wins | McNemar (exact) | Mean decisions R → RD |
|---|---|---|---|---|---|---|
| 1 | 83.5 % | 93.0 % | 7 | 26 | p = 0.001 | 11.7 → 10.6 |
| 2 | 100 % | 100 % | 0 | 0 | — | 19.8 → 13.2 |
| 3 | 87.5 % | 95.5 % | 7 | 23 | p = 0.005 | 26.6 → 22.6 |

This is the first evidence that a closed-loop, parameterized skill raises
success under the same decision budget (§3 property 6) — here with a
hand-written skill. Remaining RD failures (8/200 on tier 3) are a talk refusal
with patience already low before the skill starts. Whether an LLM can
*induce* such a skill (C) is the open question for weeks 8–9.

Caveat: one of three hand-written drafts (v2, §7.2) was no better than R.
Skill quality matters as much as skill structure; the week-7 scenario tests
reject that draft (§6.4). The table above was re-run after the `open_door`
precondition change in week 7; the numbers did not change.

### 9.2 Seeds and phases

- **Training:** 20 seeds, curriculum in 3 tiers (guard only → door only →
  guard+door [→ +trap]). Implemented as consecutive `run` calls where each
  phase starts from the previous phase's `library.json` (`--skills`).
- **Held-out test:** 20 seeds never seen in training (seeds ≥ 1001), entity
  probabilities drawn from the same ranges, different layouts.
- **Test-time setting:** library frozen (`--freeze`: no induction, library
  unchanged); reported twice — frozen (pure transfer) and with induction
  enabled starting from the trained library.

### 9.3 Budget

- Decision budget per episode: **D_max** (calibrated in week 5 so that A
  succeeds on ≤ ~40 % of tier-3 tasks — tuned openly, value reported). CLI
  default 40 until calibrated with live runs.
- Tick budget per episode: T_max = 500 (safety cap).
- Induction calls do not count as decisions but are capped (≤ 6 per episode,
  ≤ 3 per request) and reported separately (`induce_calls`,
  `induce_tokens_*`).

### 9.4 Metrics (per episode, CSV)

Implemented columns of `metrics.csv` (`EpisodeResult`): `condition, tier,
seed, success, end_reason, decisions, ticks, invalid_actions, parse_errors,
llm_calls, cache_hits, tokens_in, tokens_out, action_set_size, library_size,
skill_invocations, skill_successes, skill_effect_fail, skill_pre_fail,
induce_calls, skills_induced, skills_rejected, induce_tokens_in,
induce_tokens_out`. Trace lines also carry `kind` (primitive / helper /
skill), `skill_steps`, and `induction` (all attempts of an induction made
before that decision). B / C / E runs also write `library.json` (library
after the run, rewritten after every episode) and `induction.jsonl` (every
request: seed, decision, attempts with raw reply, acceptance, feedback,
test passes, tokens). Each run
directory also has `run.json` (configuration, decider, model, cache mode,
budget, summary; never the API key) and `traces/seed_N.jsonl` (per decision:
facts, action, args, reason, outcome, raw reply, tokens).

Planned columns, added with skills:

| Metric | Purpose |
|---|---|
| `success` | primary |
| `decisions_used`, `ticks_used` | efficiency |
| `llm_calls_choose`, `llm_calls_induce`, `tokens_in/out` | cost; should fall over episodes in C |
| `max_skill_depth` | expansion over time |
| `skill_invocations`, `skill_reuse_new_seed` | reuse / transfer |
| `wrong_skill_selected` | skill shadowing indicator |
| `failure_reason` | hostile / trap / budget / stuck |

### 9.5 Statistics

Implemented in `scripts/analyze.py` (standard-library Python): Wilson 95 %
intervals, exact two-sided McNemar on paired seeds, D_MAX from the
calibration curve. Checked by `scripts/test_analyze.py` (known Wilson values;
McNemar 7/23 = 0.0052 as in §9.1).

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
- Reference (week 6, no LLM): RD > R on tier 1 and 3 — confirmed offline, §9.1.

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
  Skills/Skill.cs         ✓ skill model (params, pre, effect, body)
  Skills/SkillJson.cs     ✓ JSON parser / serializer, path-specific errors
  Skills/SkillInterpreter.cs ✓ closed-loop execution, contracts, limits
  Skills/SkillActions.cs  ✓ skill → action of A_t, LLM-facing description
  Skills/HandwrittenSkills.cs + Library/handwritten.json ✓ condition D / RD library
  Skills/SkillValidator.cs ✓ static checks, closed-loop / macro form
  Skills/ScenarioTester.cs ✓ micro-room tests, SkillAdmission + feedback
  Skills/InducePrompts.cs ✓ induce_skill prompt (skill and macro variants)
  Skills/SkillInducer.cs  ✓ induction with feedback rounds, InductionPolicy (triggers, caps)
  Agent/UsableSkills.cs   ✓ "skills usable now" hint
  Agent/EpisodeReplay.cs  ✓ re-execute an episode from its trace (verification, demo)
  Llm/JsonText.cs         ✓ code-fence stripping
  Skills/Library.cs       registry + persistence
Dungeon.Cli/              ✓ play / render / run / skills / replay
Dungeon.Mars/             ✓ MARS host: replays a recorded episode for the PyGame visualizer
Dungeon.Tests/            ✓ xUnit
Visualization/            ✓ PyGame (`python main.py dungeon` for the dungeon replay)
scripts/run_protocol.sh   ✓ full experiment protocol (phases check … analyze)
scripts/analyze.py        ✓ results.md: calibration, training, held-out, McNemar, skills, variance, tokens
scripts/test_analyze.py   ✓ checks for the statistics
docs/PLAN.md, docs/TESTING.md, docs/EXPERIMENTS.md ✓
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
| Skill JSON | every statement and argument form, round trip, one test per rejection rule with exact location | unit |
| Interpreter | each statement type, branches follow fresh facts, `until` early stop and max, `fail`, invalid steps, argument functions, nested skills and depth bound, step / tick limits, episode end | unit |
| Hand-written skills | strategy switch on failure, retreat-and-retry, throw from range, coin, each door tool, ≥ 3 distinct paths for one skill | unit |
| Skill conditions | RD beats R on 40 tier-1 seeds; skill metrics and trace steps recorded | integration |
| Validator | each rejection rule has a fixture with the expected message; library valid; macro form | unit |
| ScenarioTester | accepts v1 / v3, rejects v2 on default seeds; pinned calibration counts over 40 blocks; rooms satisfy pre; deterministic; paired rooms; no side effects | unit, calibration |
| Admission | invalid skills not tested; feedback lists errors or all 20 rooms | unit |
| Induction | prompt examples valid and solution-free; no hidden parameters over 30 seeds; feedback rounds carry all earlier attempts; give-up; macro form; valid-only admission; triggers, per-obstacle and per-episode caps, stall | unit |
| B / C / E | C library persists and only grows; B resets; frozen library used on held-out seeds with 0 induction; replayed induction run reproduces library and metrics | integration |
| Trace replay | RD and C traces re-execute to identical outcomes and ticks; edited trace detected | integration |

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
| Simple rules (R) already solve everything | medium | high | R is 87.5 % on tier 3 (200 seeds); RD with a closed-loop skill is 95.5 % (p = 0.005), so there is headroom skills can use. Report C vs. R honestly (H4) |
| LLM-induced skills are worse than hand-written ones | high | high | One of three hand-written `calm_guard` drafts was no better than R. Calibrated scenario tests (§6.4) reject that draft in 36/40 seed blocks; RD is the reference for what a good skill achieves |
| Scenario tester misjudges skills | medium | high | Measured error rates (§6.4): ~5 % false reject, ~10 % false accept on the calibration drafts. Report accepted skills' full-episode performance, not only their test pass rate |
| Tester rooms differ from real dungeons | medium | medium | Start distances calibrated on real first sightings; one entity per room. Multi-entity situations are untested — a stated limitation |
| Environment work overruns | high | high | Guard + Door only until week 10; checkpoint at week 8 |
| API cost overrun | low | medium | Hard caps, cache, cost estimate in week 5 |
| Non-reproducible results | medium | high | Record/replay cache; pinned snapshot; seeds in every CSV row |
| Information leakage via verification | medium | medium | Verify in generated micro-rooms, never in a clone of the real episode |
| MARS integration friction | medium | low | World core independent of MARS; MARS only hosts the replay demo (built in week 9, works) |

### 14.1 Robustness audit (week 9)

Done by probing failure modes, not by reading code: hostile inputs, garbage
model output, interrupted and concurrent runs, corrupt files, and every seed
the protocol uses. Each finding has a regression test in
`Dungeon.Tests/RobustnessTests.cs`.

| # | Finding | Effect before the fix | Fix |
|---|---|---|---|
| 1 | `.env` inline comments (as in `.env.example`) were part of the value | provider `"openai   # openai \| stub …"` → *every* live run failed with "unknown provider" | `" #"` starts a comment in unquoted values; test parses the shipped `.env.example` |
| 2 | A model refusal or empty output threw | one refusal aborted the whole run | refusal / empty / truncated output becomes the reply text; it costs one decision or one induction attempt |
| 3 | 429 responses ignored `Retry-After` | retries could fire before the rate limit reset and exhaust attempts | wait = `Retry-After` if given, else exponential backoff; capped at 60 s; 6 attempts |
| 4 | Corrupt cache entry (e.g. disk full) threw `JsonException` | run crashed; in record mode the entry could never be repaired | record / live: treated as a miss and rewritten; replay: clear error naming the file |
| 5 | Cache temp file name was shared | two processes recording the same prompt crashed one another (3 of 4 in a test) | unique temp file per write, atomic rename |
| 6 | Ctrl-C (SIGINT) and `kill` / `timeout` (SIGTERM) killed the process | no `run.json`, so the protocol could not tell the run was incomplete | first SIGINT or SIGTERM stops between decisions and writes `run.json` with `aborted_reason: interrupted` (exit 1) |
| 7 | `--out` on a used directory mixed old and new traces | replay and analysis would read stale episodes as part of the run | non-empty output directory is an error; the protocol moves a failed attempt to `<name>.failed-<time>` and analysis ignores those |
| 8 | Admission accepted skills with trivial effects (`state(g) == alert`, `dist(g) > 1`) | 7 of 11 random skills that passed tests had effects reached by doing nothing useful; they would sit in the library as "learned" | effects must claim progress: guard calm/bribed/distracted, door open, item held, or exit reached |
| 9 | A library file whose skill is named like a built-in action (or calls an unknown one) | uncaught exception at startup | `SkillJson.LoadLibrary` checks names and calls on load with a clear message |
| 10 | Protocol: a failing run printed its last lines and continued | later phases ran on missing libraries | each run's full output goes to `$OUT/logs/<run>.log`; a non-zero exit stops the phase; a missing `--skills` input is reported by name |
| 11 | 8 of 20 000 tier-3 seeds (first: 1936) threw "only 2/3 chokepoints" | any run over such a seed crashed (found by running 5 000 seeds) | the seed gets an alternative layout from stream `layout#1`, `#2`, …; seeds that generated before are unchanged (pinned by a test); 0 of 20 000 fail per tier |
| 12 | `run_protocol.sh` depended on the working directory | started from elsewhere, every path was wrong | the script changes to the repository root itself |

**Checked and found robust:**
- Fuzzing: 1 000 episodes of random model replies (bad JSON, wrong types,
  unknown actions and ids, nested objects) never crashed; 1 000 more are
  part of the suite (300 per run).
- 600 random type-correct skills through validation and 5 080 scenario test
  rooms never crashed; slowest admission 36 ms.
- Argument extremes (`go_to(x, 100000)`, negative distances, `int.MaxValue`
  coordinates) return `invalid` or `unreachable` in < 5 ms.
- Skill parser and condition parser on deep nesting (JSON depth limit
  enforced), huge numbers, empty input.
- All 150 seeds the protocol uses generate, and so do 20 000 per tier.
- Headline numbers unchanged by every fix: R 175/200 and RD 191/200 on tier
  3; the stub dry run (25 runs) replays without a single mismatch.
- An interrupted protocol resumes: finished runs are skipped, the
  interrupted one is redone, the cache makes repeated prompts free.

**Remaining known limits (not fixed, stated here):** a second Ctrl-C kills
the process without `run.json` (by design); the stub's results depend on
prompt wording, so they are pipeline checks only; scenario rooms contain one
entity each (§14 risk table).

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
