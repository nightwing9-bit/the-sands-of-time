#!/usr/bin/env bash
# Explicit PR-only R5 research phases. The ordinary pipeline never downloads cohort labels.
set -euo pipefail

phase=${1:?Select or evaluate phase required}
mapfile -t requests < <(awk '/^R5-offline-experiment: / { sub(/\r$/, ""); print }' <<< "${R5_PR_BODY:?PR body required}")
[[ "${#requests[@]}" == 1 ]]
read -r marker requested_phase cohort_run cohort_hash selection_run freeze_hash extra <<< "${requests[0]}"
[[ "$marker" == R5-offline-experiment: && "$phase" == "$requested_phase" && -z "$extra" ]]
[[ "$cohort_run" =~ ^[0-9]+$ && "$cohort_hash" =~ ^[0-9a-f]{64}$ ]]
[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]]
data="$PWD/TestResults/r5-offline-data"
output="$PWD/TestResults/r5-offline-$phase"
mkdir -p "$data" "$output"
export R5_COHORT_INPUT="$data" R5_COHORT_SHA="$cohort_hash" R5_EXPERIMENT_OUTPUT="$output"
export R5_EXPERIMENT_REVISION=${R5_EXPERIMENT_REVISION:?exact PR head SHA required}
[[ "$R5_EXPERIMENT_REVISION" =~ ^[0-9a-f]{40}$ ]]
download() {
  gh run download "$1" --name "$2" --dir "$3"
}
verify_digest() {
  printf '%s  %s\n' "$1" "$2" | sha256sum --check --strict
}
run_case() {
  local category="$1" method="$2" result="$3"
  dotnet test tests/Revenant/Revenant.Tests.csproj --configuration Release --no-build \
    --filter "Category=$category&FullyQualifiedName~$method" \
    --logger "console;verbosity=normal" --logger "trx;LogFileName=$result.trx" --results-directory "$output"
  python3 - "$output/$result.trx" "$method" <<'PY'
import sys
import xml.etree.ElementTree as ET
report = ET.parse(sys.argv[1])
counters = report.find(".//{*}Counters").attrib
cases = report.findall(".//{*}UnitTestResult")
assert len(cases) == 1 and all(counters[k] == "1" for k in ("total", "executed", "passed"))
assert cases[0].attrib["outcome"] == "Passed" and cases[0].attrib["testName"].endswith("." + sys.argv[2])
PY
}
download "$cohort_run" r5-native-cohort-evidence "$data"
verify_digest "$cohort_hash" "$data/manifest.bin"
case "$phase" in
  select)
    [[ -z "$selection_run$freeze_hash" ]]
    download "$cohort_run" r5-cohort-development "$data"
    run_case OfflineTraining Select_R5_candidate_using_development_splits_only selection
    if [[ ! -f "$output/selection.json" ]]; then
      echo 'R5 scientific guard failed; no calibration labels were downloaded.'
      exit 0
    fi
    # Architecture and ZIP hash are now recorded. Only at this point may Calibration be opened.
    download "$cohort_run" r5-cohort-calibration "$data"
    export R5_CANDIDATE_INPUT="$output"
    run_case OfflineTraining Calibrate_R5_selection_in_a_fresh_process calibration
    sha256sum "$output/freeze.json" | tee "$output/freeze.sha256"
    ;;
  evaluate)
    [[ "$selection_run" =~ ^[0-9]+$ && "$freeze_hash" =~ ^[0-9a-f]{64}$ ]]
    export R5_CANDIDATE_INPUT="$PWD/TestResults/r5-frozen-input" R5_FREEZE_SHA="$freeze_hash"
    mkdir -p "$R5_CANDIDATE_INPUT"
    download "$selection_run" r5-offline-selection "$R5_CANDIDATE_INPUT"
    verify_digest "$freeze_hash" "$R5_CANDIDATE_INPUT/freeze.json"
    artifact_hash=$(jq -er '.Selection.CandidateSha256 | select(test("^[0-9a-f]{64}$"))' "$R5_CANDIDATE_INPUT/freeze.json")
    verify_digest "$artifact_hash" "$R5_CANDIDATE_INPUT/candidate.zip"
    jq -e --arg sha "$cohort_hash" '.Selection.DatasetSha256 == $sha' "$R5_CANDIDATE_INPUT/freeze.json"
    export R5_DATASET_MODE=challenge R5_CODE_REVISION="$R5_EXPERIMENT_REVISION"
    export R5_CHALLENGE_INPUT="$PWD/TestResults/r5-ring-data" R5_DATASET_OUTPUT="$PWD/TestResults/r5-ring-data"
    run_case R5Cohort Generate_native_R5_cohort ring-financial-cohort
    export R5_CHALLENGE_SHA
    R5_CHALLENGE_SHA=$(sha256sum "$R5_CHALLENGE_INPUT/manifest.bin" | cut -d ' ' -f 1)
    cp "$R5_CHALLENGE_INPUT/manifest.json" "$output/ring-manifest.json"
    cp "$R5_CHALLENGE_INPUT/manifest.bin" "$output/ring-manifest.bin"
    # Fetch the independent final labels only after validating the already-frozen model and completing the challenge.
    download "$cohort_run" r5-cohort-final-test "$data"
    run_case OfflineTraining Evaluate_frozen_R5_model_on_FinalTest_and_the_independent_ring_challenge evaluation
    ;;
  *) echo "Unknown phase: $phase" >&2; exit 2 ;;
esac
