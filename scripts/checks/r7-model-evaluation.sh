#!/usr/bin/env bash
set -euo pipefail

: "${R7_PR_BODY:?Missing PR controls}"
: "${REVENANT_R7_SOURCE_SHA:?Missing source revision}"
output="$PWD/TestResults/r7-model-evaluation"
export REVENANT_R7_COHORT_INPUT="$PWD/TestResults/r7-cohort-input"
export REVENANT_R7_MODEL_OUTPUT="$output"
mkdir -p "$output"
select_line=$(printf '%s\n' "$R7_PR_BODY" | sed -nE 's/^R7-select-models: ([0-9]+)$/\1/p')
final_line=$(printf '%s\n' "$R7_PR_BODY" | sed -nE 's/^R7-evaluate-frozen: ([0-9]+ [0-9]+ [0-9a-f]{64})$/\1/p')
if [[ "$select_line" =~ ^[0-9]+$ && -z "$final_line" ]]; then
  export REVENANT_R7_COHORT_RUN="$select_line"
  partitions=(Train Validation Calibration)
  case_name=Select_on_validation_and_freeze_calibration_before_final_data
  expected=frozen.json
elif [[ -n "$final_line" && -z "$select_line" ]]; then
  read -r REVENANT_R7_COHORT_RUN frozen_run REVENANT_R7_FROZEN_SHA256 <<< "$final_line"
  export REVENANT_R7_COHORT_RUN REVENANT_R7_FROZEN_SHA256
  export REVENANT_R7_FROZEN_INPUT="$PWD/TestResults/r7-frozen-input"
  gh run download "$frozen_run" -n r7-model-evaluation -D "$REVENANT_R7_FROZEN_INPUT"
  printf '%s  %s\n' "$REVENANT_R7_FROZEN_SHA256" "$REVENANT_R7_FROZEN_INPUT/frozen.json" | sha256sum --check
  rg -q '<Counters [^>]*total="1" executed="1" passed="1" failed="0"' "$REVENANT_R7_FROZEN_INPUT/model-evaluation.trx"
  partitions=(FinalTest Challenge)
  case_name=Evaluate_the_frozen_SSA_models_on_final_and_shifted_histories
  expected=final-evaluation.json
else
  echo 'Choose exactly one R7 model evaluation control.' >&2
  exit 2
fi

download_args=()
for split in "${partitions[@]}"; do
  download_args+=(--pattern "r7-cohort-$split-*")
done
gh run download "$REVENANT_R7_COHORT_RUN" "${download_args[@]}" -D "$REVENANT_R7_COHORT_INPUT"
for report in "$REVENANT_R7_COHORT_INPUT"/r7-cohort-*/qualification.trx; do
  rg -q '<Counters [^>]*total="1" executed="1" passed="1" failed="0"' "$report"
done

test "$(uname -s)" = Linux
test "$(uname -m)" = x86_64
sudo apt-get update -qq
sudo apt-get install -y -qq --no-install-recommends libomp-dev
dotnet test tests/Revenant/Revenant.Tests.csproj --configuration Release \
  --filter "Category=R7Longitudinal&FullyQualifiedName~$case_name" \
  --logger 'trx;LogFileName=model-evaluation.trx' --results-directory "$output"
rg -q '<Counters [^>]*total="1" executed="1" passed="1" failed="0"' "$output/model-evaluation.trx"
rg -q "<UnitTestResult [^>]*testName=\"[^\"]*\.$case_name\"[^>]*outcome=\"Passed\"" "$output/model-evaluation.trx"
test -s "$output/$expected"
sha256sum "$output/$expected"
