# Running the Experiments

How to run the full study on a fresh machine and turn it into the report tables. Everything
here is offline-testable first (`PROVIDER=stub`), so the expensive live run is the last step.
Design and definitions: `docs/PLAN.md`; manual checks: `docs/TESTING.md`.

## 1. Setup on a new machine

```bash
git clone https://github.com/youngleee/blueprint-grid && cd blueprint-grid
# .NET 10 SDK: https://dotnet.microsoft.com/download  (macOS: brew install --cask dotnet-sdk)
dotnet --version                 # 10.x
python3 --version                # 3.8+ (analysis script, standard library only)
cp .env.example .env             # then set OPENAI_API_KEY=... in .env
scripts/run_protocol.sh check    # build, 359 tests, skill admission, key present
```

`check` must print `Passed!`, two `accepted` skills and `OPENAI_API_KEY found`.

## 2. Dry run (no key, no cost, ~5 min)

Runs every phase with the offline stub on small seed sets, to prove the machine and the pipeline
work before spending money:

```bash
PROVIDER=stub OUT=runs/dry TRAIN1=1-5 TRAIN2=6-10 TRAIN3=11-15 HELDOUT=1001-1010 \
  CAL_SEEDS=2001-2010 REPEATS=2 scripts/run_protocol.sh all variance
open runs/dry/results.md         # or any Markdown viewer
```

The report starts with a "Stub provider" warning; the numbers are pipeline checks only.

## 3. The live protocol

```bash
scripts/run_protocol.sh check
scripts/run_protocol.sh calibrate       # A, R, RD with D_CAL=80 on CAL_SEEDS
python3 scripts/analyze.py runs/protocol   # read "Calibration", choose D_MAX
D_MAX=<value> scripts/run_protocol.sh train test variance analyze
```

| Phase | Runs | What it answers |
|---|---|---|
| `calibrate` | `cal_A`, `cal_R`, `cal_RD` on 30 tier-3 seeds, 80 decisions | success as a function of the budget; pick D_MAX (PLAN §9.3) |
| `train` | `train_<C,E,C_valid,E_valid>_t1..t3` | curriculum tier 1 → 2 → 3, each phase starting from the previous `library.json` |
| `test` | `test_R, RD, A, D, B, C_frozen, C_induce, E_frozen, C_valid_frozen, E_valid_frozen` | held-out tier-3 seeds 1001–1050 at D_MAX |
| `variance` | `var_C_frozen_1..3` | live repeats of frozen C with fresh caches |
| `analyze` | `runs/protocol/results.md` | all tables below |

Seed sets are disjoint: training 1–60, held-out 1001–1050, calibration 2001–2030. Every run writes
`run.json`, `metrics.csv`, `traces/`, and for B/C/E also `library.json` and `induction.jsonl`
(formats: `docs/TESTING.md` → *Experiment runs*).

### Choosing D_MAX

The rule (PLAN §9.3) is "the largest budget at which A succeeds on ≤ 40 % of tier-3 seeds".
`analyze.py` computes it from the calibration runs. If A stays below 40 % at every budget
(it does with the stub), use the budget where R and RD stop improving instead, and say so in
the report. Do not tune D_MAX on the held-out seeds.

### Resuming

A run is skipped if its `run.json` exists without `aborted_reason`. Aborted or missing runs are
redone; the LLM cache (`cache/llm/openai/`, record mode) makes every prompt answered before free,
so an interrupted protocol costs nothing extra to resume. To redo a finished run, delete its
directory.

## 4. Cost estimate

Measured with the stub prompts (same prompt text as the live runs): ≈ 780 input + 15 output
tokens per decision call, ≈ 1 650 input + up to 1 500 output tokens per induction call. A full
episode is 10–40 decision calls.

| Phase | Episodes | Decision calls (≤) | Induction calls (≤) |
|---|---|---|---|
| calibrate | 30 LLM (A) | 2 400 | 0 |
| train (4 curricula × 60 seeds) | 240 | 9 600 | 1 440 |
| test (8 LLM conditions × 50 seeds) | 400 | 16 000 | ~600 |
| variance (3 × 50) | 150 | 6 000 | 0 |

Upper bound ≈ 34 000 decision calls ≈ 27 M input tokens, plus ≈ 2 000 induction calls ≈ 3 M input /
3 M output tokens. Look up the current per-token price of the model in `.env`
(`OPENAI_MODEL`, default `gpt-4.1-mini-2025-04-14`) and multiply. Real runs end early on success,
so the actual figure is typically 40–60 % of the bound. The hard caps per run (`MAX_CALLS=3000`,
`MAX_TOKENS=6000000`) stop a run cleanly before it can exceed that; raise them only knowingly.

To halve the cost: `HELDOUT=1001-1025`, drop the `*_valid` curricula (edit `phase_train`), or
`REPEATS=2`.

## 5. Reading `results.md`

| Section | Look for |
|---|---|
| Calibration | A's success curve vs budget; the D_MAX line |
| Training | `attempts accepted` (can the model write admissible skills at all?), library size per tier, LLM calls per 5-episode block (H3: falling for C) |
| Held-out results | success with Wilson 95 % intervals; failure reasons |
| Paired comparison | H1 `C_frozen` vs `A`, H2 `C_frozen` vs `E_frozen` (and the `valid` pair), H4 `C_frozen` vs `R`; exact McNemar p on the same seeds |
| Skill use | which induced skills are actually called and how often they succeed |
| Live variance | spread of frozen C across fresh-cache repeats |

Then inspect the learned skills directly:

```bash
dotnet run --project Dungeon.Cli -- skills --test --file runs/protocol/train_C_t3/library.json
```

and read a few `induction.jsonl` lines to see how the model responded to admission feedback.
These are the qualitative trace of PLAN §9.6.

## 6. What to bring back

Commit nothing from `runs/` or `cache/` (both ignored). Archive instead:

```bash
tar czf protocol_$(date +%Y%m%d).tgz runs/protocol cache
```

The cache makes every number in the report reproducible offline with `--llm replay` on any
machine: copy the archive, extract it in the repository root, and rerun any phase.
