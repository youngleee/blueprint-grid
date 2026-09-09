#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

# Build first so dependency warnings stay out of the simulation log.
dotnet build

# Keep a separate log for each run while also showing output in the terminal.
mkdir -p logs
log_path="$PWD/logs/run-$(date +%Y%m%d-%H%M%S)-$$.log"
scenario="${1:-config.adaptive.visual.json}"
printf 'Scenario: %s\n' "$scenario" | tee "$log_path"
dotnet run --no-build -- "$scenario" 2>&1 | tee -a "$log_path"

# Open the completed log in the default macOS text editor.
printf '\nRun log: %s\n' "$log_path"
if [[ "$(uname -s)" == "Darwin" ]]; then
    open -t "$log_path" || printf 'Could not open the editor; use the log path above.\n'
fi
