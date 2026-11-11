#!/usr/bin/env bash
# Spec 08 task 8.2: mint the committed risk-classifier fixture artifact (OFFLINE; never run by normal CI).
#   scripts/checks/train-risk-model.sh            # writes src/Revenant/Intelligence/Artifacts/risk-fixture-fasttree-v1
#   scripts/checks/train-risk-model.sh <out-dir>  # writes somewhere else (e.g. a scratch directory)
# Trains ML.NET FastTree on the seeded, labeled "project synthetic fixture" only (no network, no PaySim, no raw dataset).
# Refuses to overwrite an existing artifact: pinned files are immutable, a retrain is a new model id.
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="${1:-$REPO_ROOT/src/Revenant/Intelligence/Artifacts/risk-fixture-fasttree-v1}"
# shellcheck source=scripts/checks/dotnet-env.sh
source "$REPO_ROOT/scripts/checks/dotnet-env.sh"
mkdir -p "$OUT"
cd "$REPO_ROOT"
REVENANT_RISK_MODEL_OUT="$OUT" dotnet test tests/Revenant -c Release --nologo \
  --filter "Category=OfflineTraining" --logger "console;verbosity=detailed"
echo "train-risk-model: artifact directory $OUT"
ls -l "$OUT"
