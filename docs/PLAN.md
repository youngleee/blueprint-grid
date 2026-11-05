# Project Plan: Action Space Expansion in an LLM Dungeon Agent

Master's semester project (~15 weeks). Status: **draft for supervisor review**.

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

- One custom PRNG (SplitMix64 or xoshiro128) with **copyable state**;
  `System.Random` cannot be cloned or reliably reseeded mid-run.
- Separate streams per purpose: `layoutRng`, `entityRng`, `outcomeRng`.
  Changing an entity's behaviour does then not change the layout for the same
  seed.

### 5.3 Map

- Procedural: 4–6 rooms connected by corridors, total ≤ 40×40 cells.
- Goal: reach the exit cell (`goal`) in the last room.
- **Sight radius r = 3** (Chebyshev), walls block line of sight (simple
  Bresenham). The agent keeps a memory of seen cells (fog of war).
- Generator guarantees solvability: at least one obstacle path to the exit is
  passable given the items placed on the map (checked by a solver on the full
  state at generation time).

### 5.4 Entities (MUST: Guard, Door; SHOULD: Trap)

All transitions use `entityRng` / `outcomeRng`; probabilities are per-seed
parameters drawn from ranges so held-out seeds behave differently.

**Guard** — blocks a corridor/doorway cell.

| State | Transition | Probability (range) |
|---|---|---|
| `idle` | agent within 2 cells and visible → `alert` | 1.0 |
| `alert` | `talk` → `idle` (calmed) | p_calm ∈ [0.3, 0.7] |
| `alert` | `give(food)` → `distracted(k)` | p_bribe_food ∈ [0.5, 0.9] |
| `alert` | `give(coin)` → `bribed` (permanent, leaves post) | p_bribe_coin ∈ [0.2, 0.8] |
| `alert` | `throw(item, cell)` away from guard → `distracted(k)`, guard walks to cell | p_throw ∈ [0.6, 1.0] |
| `alert` | 3 consecutive ticks adjacent without a successful interaction → `hostile` | 1.0 |
| `distracted(k)` | after k ticks → `alert` | k ∈ [3, 6] |
| `hostile` | agent adjacent → episode fails | 1.0 |

**Door**

| Type | Interaction | Probability (range) |
|---|---|---|
| `locked` | `use(key, door)` with matching key → `open` | 1.0 |
| `locked` | `use(lockpick, door)` → `open` | p_pick ∈ [0.2, 0.5] per attempt |
| `jammed` | `use(none, door)` (push) → `open` | p_push ∈ [0.3, 0.6] per attempt |

**Trap (SHOULD)** — hidden; `look` reveals it within radius 1 with p_detect ∈
[0.4, 0.8]; `use(tool, trap)` disarms; stepping on it fails the episode.

**Items:** `key(id)`, `lockpick`, `food`, `coin`, `tool` — placed in rooms,
picked up with `pick_up`.

### 5.5 Primitives (trusted, hard-coded)

| Primitive | Arguments | Notes |
|---|---|---|
| `move` | `dir ∈ {N,E,S,W}` | fails silently into walls/entities |
| `look` | — | reveals hidden traps within 1 cell (probabilistic) |
| `pick_up` | — | picks item on current cell |
| `give` | `item, target` | target must be adjacent |
| `talk` | `target` | target within 2 cells |
| `use` | `item?, target` | doors, traps |
| `throw` | `item, cell` | cell within 3, line of sight |
| `wait` | — | |

Each primitive costs 1 tick. A primitive **chosen by the LLM** costs 1 decision.

### 5.6 Observation → facts

Each step the agent receives a symbolic fact list, never the raw map:

```
at(agent, 12, 7)
visible(guard_1, 14, 7)  state(guard_1) = alert  dist(guard_1) = 2
visible(door_2, 12, 4)   type(door_2) = locked
has(food)  has(lockpick)
seen_exit = false
explored_rooms = 2/5   frontier = [(15,9), (10,3)]
last_action = talk(guard_1) -> no_change
```

*Why facts:* keeps prompts small (cheap model), makes preconditions checkable
by code, and stops the LLM from doing pathfinding — navigation is handled by
the built-in helper `go_to(target)` (§6.2), so the research question is about
interaction skills, not path planning.

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

### 6.2 Built-in helpers (not counted as learned skills)

- `go_to(entity|cell)`: A* on *known* cells.
- `explore()`: move to nearest frontier.

These are given to **all conditions** so that navigation is not the variable
under test. Stated explicitly in the report.

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

Cheap OpenAI model (e.g. `gpt-4.1-mini` or the current mini tier). **Pin a
dated snapshot** in config and report it. Temperature 0, JSON output mode.
Decide final model in week 1 by running 20 calls of each prompt type and
checking JSON validity.

### 8.2 Two prompt types

| Prompt | Frequency | Output |
|---|---|---|
| `choose_action` | every decision | `{"action": name, "args": [...], "reason": "..."}` |
| `induce_skill` | rare | one DSL skill |

### 8.3 Record/replay cache (reproducibility)

- Key: SHA-256 of `(model, temperature, prompt_type, full prompt text)`.
- Store: `cache/llm/<hash>.json` with request, response, token usage, time.
- Modes: `live` (call + write), `replay` (read only, fail on miss),
  `record` (call on miss).
- A seeded run in `replay` mode is bit-for-bit reproducible without network.
  Live variance is measured separately (§9.5).

### 8.4 Cost control

- Hard caps per run: max calls, max tokens; runner aborts cleanly.
- Log tokens per call; report total cost per condition.
- Estimate in week 5 from real calls and adjust episode counts before
  committing to the full experiment.

## 9. Experimental design

### 9.1 Conditions

| ID | Name | Induction | Library persists | Skill form |
|---|---|---|---|---|
| **A** | Primitives only | no | — | — |
| **B** | Induction, reset each episode | yes | no | DSL |
| **C** | Induction, persistent library | yes | yes | DSL |
| **E** | Macro ablation | yes | yes | macro (§7.3) |
| D (could) | Hand-written library | no | fixed | DSL |

Must: A, C, E. Should: B. Could: D.

### 9.2 Seeds and phases

- **Training:** 20 seeds, curriculum in 3 tiers (guard only → door only →
  guard+door [→ +trap]).
- **Held-out test:** 20 seeds never seen in training, entity probabilities drawn
  from the same ranges, different layouts.
- **Test-time setting:** library frozen; reported twice — induction disabled
  (pure transfer) and induction enabled.

### 9.3 Budget

- Decision budget per episode: **D_max** (calibrated in week 5 so that A
  succeeds on ≤ ~40 % of tier-3 tasks — tuned openly, value reported).
- Tick budget per episode: T_max = 500 (safety cap).
- Induction calls do not count as decisions but are capped (≤ 3 per episode)
  and reported separately.

### 9.4 Metrics (per episode, CSV)

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

A negative result for any hypothesis is reported as such.

## 10. Software architecture

```
GridBlueprint/            (existing project, MARS host)
  Program.cs              CLI: --demo <config> | --experiment <config> | --replay
Dungeon/                  (new class library, no MARS dependency)
  Core/Rng.cs             copyable PRNG, named streams
  Core/DungeonWorld.cs    state, Step(action), Clone()
  Core/Generator.cs       seeded rooms, entities, items, solvability check
  Core/Visibility.cs      sight radius, line of sight, memory
  Entities/Guard.cs, Door.cs, Trap.cs
  Agent/Facts.cs          observation → facts; condition evaluator
  Agent/Primitives.cs
  Agent/Helpers.cs        go_to, explore (A* on known cells)
  Skills/Skill.cs         model + JSON (de)serialization
  Skills/Interpreter.cs   executes DSL, returns ticks + outcome
  Skills/Validator.cs     static checks
  Skills/ScenarioTester.cs generated micro-room tests
  Skills/Library.cs       registry + persistence
  Llm/ILlmClient.cs, OpenAiClient.cs, CachingClient.cs
  Llm/Prompts.cs
  Agent/LlmAgent.cs       decision loop, induction trigger
  Experiment/Runner.cs, Conditions.cs, MetricsWriter.cs
Dungeon.Tests/            NUnit or xUnit
Visualization/            PyGame (extended: fog of war, entities, skill log)
docs/PLAN.md
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
| Rng | same seed → same sequence; clone continues identically | unit |
| Generator | 1 000 seeds: solvable, connected, item for every lock | property |
| Entities | each transition table row, incl. probability edges 0 / 1 | unit |
| Visibility | walls block sight; radius boundary | unit |
| Interpreter | each statement type; `max` bound enforced; failure propagation | unit |
| Validator | each rejection rule has a failing fixture | unit |
| ScenarioTester | known-good skill passes; known-bad skill fails | unit |
| Cache | replay reproduces live run exactly | integration |
| End to end | scripted fake LLM solves tier-1 seed; replay of recorded run matches metrics | integration |

A deterministic **scripted LLM stub** (rule-based policy, not canned answers)
is used for tests and CI only; it is never reported as a result.

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
