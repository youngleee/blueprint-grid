#!/usr/bin/env bash
# Full experiment protocol (docs/PLAN.md §9, docs/EXPERIMENTS.md). Run from the repository root.
#
#   scripts/run_protocol.sh <phase> [<phase> ...]
#
# Phases (run in this order the first time):
#   check        build, run the test suite, print settings (no LLM calls)
#   calibrate    A, R, RD on tier 3 with a large decision budget, to choose D_MAX
#   train        curriculum for C, E and C/E-valid: tier 1 -> 2 -> 3, library carried between phases
#   test         held-out tier-3 evaluation of every condition at D_MAX
#   variance     repeat frozen C on the held-out seeds with fresh caches (live variance)
#   verify       re-execute every recorded episode from its trace (no LLM) and check it matches
#   analyze      write $OUT/results.md from everything above
#   all          check calibrate train test verify analyze
#
# Environment (all optional):
#   PROVIDER=openai|stub   stub = offline dry run of the whole protocol, no key, no cost (default openai)
#   OUT=runs/protocol      output root
#   D_MAX=40               decision budget for train/test (set it from the calibrate phase)
#   D_CAL=80               decision budget for the calibrate phase
#   TRAIN1=1-20 TRAIN2=21-40 TRAIN3=41-60    training seeds per tier
#   HELDOUT=1001-1050      held-out tier-3 seeds (never used in training)
#   CAL_SEEDS=2001-2030    calibration seeds (disjoint from training and held-out)
#   REPEATS=3              variance repeats
#   MAX_CALLS=3000 MAX_TOKENS=6000000   hard caps per run (real API calls only)
#
# Every run writes its own directory under $OUT. Re-running a phase reuses the LLM cache
# (record mode): already answered prompts cost nothing, so an interrupted protocol can be resumed.
set -euo pipefail

PROVIDER="${PROVIDER:-openai}"
OUT="${OUT:-runs/protocol}"
D_MAX="${D_MAX:-40}"
D_CAL="${D_CAL:-80}"
TRAIN1="${TRAIN1:-1-20}"
TRAIN2="${TRAIN2:-21-40}"
TRAIN3="${TRAIN3:-41-60}"
HELDOUT="${HELDOUT:-1001-1050}"
CAL_SEEDS="${CAL_SEEDS:-2001-2030}"
REPEATS="${REPEATS:-3}"
MAX_CALLS="${MAX_CALLS:-3000}"
MAX_TOKENS="${MAX_TOKENS:-6000000}"

CLI_DLL="Dungeon.Cli/bin/Release/net10.0/Dungeon.Cli.dll"

log() { printf '\n=== %s ===\n' "$*"; }

build() {
  dotnet build GridBlueprint.sln -c Release -v quiet -nologo >/dev/null
}

cli() {
  dotnet "$CLI_DLL" "$@"
}

# run <name> <args...>: one experiment run into $OUT/<name>; skipped if it already finished.
run() {
  local name="$1"; shift
  local dir="$OUT/$name"
  # Finished = run.json written and no abort recorded (aborted runs are redone; the cache makes
  # already answered prompts free).
  if [[ -f "$dir/run.json" ]] && ! grep -q '"aborted_reason"' "$dir/run.json"; then
    echo "skip $name (finished; delete $dir to rerun)"
    return 0
  fi
  echo "run  $name: $*"
  cli run "$@" --provider "$PROVIDER" --max-calls "$MAX_CALLS" --max-tokens "$MAX_TOKENS" --out "$dir" | tail -3
}

phase_check() {
  log "check"
  dotnet --version
  build
  dotnet test Dungeon.Tests -c Release -v quiet -nologo 2>&1 | grep -E "Passed!|Failed!" || true
  cli skills --test | grep -E "accepted|rejected"
  if [[ "$PROVIDER" == "openai" ]]; then
    if [[ -f .env ]] && grep -q '^OPENAI_API_KEY=.\+' .env; then
      echo "OPENAI_API_KEY found in .env"
    elif [[ -n "${OPENAI_API_KEY:-}" ]]; then
      echo "OPENAI_API_KEY found in environment"
    else
      echo "ERROR: no OPENAI_API_KEY (copy .env.example to .env), or use PROVIDER=stub" >&2
      exit 2
    fi
  fi
  echo "provider=$PROVIDER out=$OUT D_MAX=$D_MAX held-out=$HELDOUT"
}

phase_calibrate() {
  log "calibrate (tier 3, seeds $CAL_SEEDS, D=$D_CAL)"
  build
  run cal_A  --condition A  --tier 3 --seeds "$CAL_SEEDS" --max-decisions "$D_CAL"
  run cal_R  --condition R  --tier 3 --seeds "$CAL_SEEDS" --max-decisions "$D_CAL"
  run cal_RD --condition RD --tier 3 --seeds "$CAL_SEEDS" --max-decisions "$D_CAL"
  echo "Next: python3 scripts/analyze.py $OUT  (section 'Calibration'), then set D_MAX."
}

# train <label> <condition> [extra args]: curriculum tier 1 -> 2 -> 3, library carried forward.
train() {
  local label="$1" cond="$2"; shift 2
  run "train_${label}_t1" --condition "$cond" --tier 1 --seeds "$TRAIN1" --max-decisions "$D_MAX" "$@"
  run "train_${label}_t2" --condition "$cond" --tier 2 --seeds "$TRAIN2" --max-decisions "$D_MAX" \
      --skills "$OUT/train_${label}_t1/library.json" "$@"
  run "train_${label}_t3" --condition "$cond" --tier 3 --seeds "$TRAIN3" --max-decisions "$D_MAX" \
      --skills "$OUT/train_${label}_t2/library.json" "$@"
}

phase_train() {
  log "train (D_MAX=$D_MAX)"
  build
  train C       C
  train E       E
  train C_valid C --admission valid
  train E_valid E --admission valid
}

phase_test() {
  log "held-out test (tier 3, seeds $HELDOUT, D_MAX=$D_MAX)"
  build
  local t=(--tier 3 --seeds "$HELDOUT" --max-decisions "$D_MAX")
  run test_R  --condition R  "${t[@]}"
  run test_RD --condition RD "${t[@]}"
  run test_A  --condition A  "${t[@]}"
  run test_D  --condition D  "${t[@]}"
  run test_B  --condition B  "${t[@]}"
  run test_C_frozen       --condition C "${t[@]}" --freeze --skills "$OUT/train_C_t3/library.json"
  run test_C_induce       --condition C "${t[@]}"          --skills "$OUT/train_C_t3/library.json"
  run test_E_frozen       --condition E "${t[@]}" --freeze --skills "$OUT/train_E_t3/library.json"
  run test_C_valid_frozen --condition C "${t[@]}" --freeze --skills "$OUT/train_C_valid_t3/library.json"
  run test_E_valid_frozen --condition E "${t[@]}" --freeze --skills "$OUT/train_E_valid_t3/library.json"
}

phase_variance() {
  log "variance ($REPEATS repeats of frozen C, fresh cache each)"
  build
  for i in $(seq 1 "$REPEATS"); do
    # Own cache per repeat, so each repeat is a fresh set of live calls (temperature 0 can still vary).
    DUNGEON_LLM_CACHE="cache/variance_$i" run "var_C_frozen_$i" --condition C --tier 3 --seeds "$HELDOUT" \
      --max-decisions "$D_MAX" --freeze --skills "$OUT/train_C_t3/library.json"
  done
}

phase_verify() {
  log "verify (replay every trace, no LLM)"
  build
  local bad=0
  for dir in "$OUT"/*/; do
    [[ -f "$dir/run.json" && -d "$dir/traces" ]] || continue
    if ! cli replay --run "$dir" --check > "$dir/replay_check.txt"; then
      echo "MISMATCH in $dir (see replay_check.txt)"; bad=1
    else
      echo "ok   $(basename "$dir") ($(grep -c 'matches trace' "$dir/replay_check.txt") episodes)"
    fi
  done
  return $bad
}

phase_analyze() {
  log "analyze"
  python3 scripts/analyze.py "$OUT" --out "$OUT/results.md"
}

[[ $# -eq 0 ]] && { sed -n '2,30p' "$0"; exit 1; }
mkdir -p "$OUT"
for phase in "$@"; do
  case "$phase" in
    check) phase_check ;;
    calibrate) phase_calibrate ;;
    train) phase_train ;;
    test) phase_test ;;
    variance) phase_variance ;;
    verify) phase_verify ;;
    analyze) phase_analyze ;;
    all) phase_check; phase_calibrate; phase_train; phase_test; phase_verify; phase_analyze ;;
    *) echo "unknown phase '$phase'" >&2; exit 1 ;;
  esac
done
