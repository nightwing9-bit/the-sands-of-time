#!/usr/bin/env bash
# Spec 08 task 8.1 ML.NET platform probe (R08.13, design.md section 1A). One repeatable command, no network beyond NuGet restore
# (and the SDK image pull for --docker-amd64).
#
#   ml-platform-probe.sh --native         FastTree fit -> save/load -> predict on THIS host (native macOS ARM64 or Linux).
#   ml-platform-probe.sh --docker-amd64   FastTree + DetectSpikeBySsa + ForecastBySsa inside `docker run --platform linux/amd64`
#                                         (official .NET 10 SDK image, Debian glibc), copy of the sources in the container, named
#                                         NuGet cache volume, bounded runtime. On Apple Silicon this is EMULATED and best-effort.
#   ml-platform-probe.sh --ci             Same three probes directly on a real Linux x64 host (the authoritative CI gate).
#
# A mode is PASSED only when the expected number of probe tests actually ran and passed (TRX counters), so a filter that
# selects nothing, or tests that were skipped, cannot be reported as success. Evidence JSON (architecture, versions, mapped native
# libraries such as libMklImports / libiomp5) is written to TestResults/ml-platform/<mode>/ (gitignored).
# Exit: 0 passed, 1 a probe failed or crashed, 2 bad arguments, 3 required tooling unavailable or wrong host for --ci.
set -uo pipefail
# shellcheck source=scripts/checks/lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

SDK_IMAGE="${ML_PROBE_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
TIMEOUT_SECONDS="${ML_PROBE_TIMEOUT_SECONDS:-1500}"
NUGET_VOLUME="${ML_PROBE_NUGET_VOLUME:-sands-ml-probe-nuget}"
MODE="${1:-}"
usage() { echo "usage: $0 --native | --docker-amd64 | --ci" >&2; }
[ $# -eq 1 ] || { usage; exit $EXIT_USAGE; }

PROJECT="tests/Revenant/Revenant.Tests.csproj"
case "$MODE" in
  --native) FILTER="Category=MlFastTree"; EXPECTED=1; NAME="native" ;;
  --docker-amd64|--ci) FILTER="Category=MlFastTree|Category=MlSsaX64"; EXPECTED=3; NAME="${MODE#--}" ;;
  *) usage; exit $EXIT_USAGE ;;
esac
OUT="$REPO_ROOT/TestResults/ml-platform/$NAME"
rm -rf "$OUT"
mkdir -p "$OUT"

# trx_check OUTDIR EXPECTED -> 0 only when exactly EXPECTED tests were executed and all of them passed.
trx_check() {
  local trx counters
  trx="$(find "$1" -name '*.trx' | head -n 1)"
  [ -n "$trx" ] || { echo "no TRX file found: the test host did not complete"; return 1; }
  counters="$(grep -o '<Counters [^>]*>' "$trx" | head -n 1)"
  echo "TRX counters: $counters"
  for want in "executed=\"$2\"" "passed=\"$2\"" 'failed="0"' 'notExecuted="0"'; do
    case "$counters" in *"$want"*) ;; *) echo "expected $want"; return 1 ;; esac
  done
}

case "$MODE" in
  --native)
    if ! need_tool dotnet || ! dotnet --version >/dev/null 2>&1; then
      echo "[UNAVAILABLE] ml-platform-probe: no usable dotnet SDK (source scripts/checks/dotnet-env.sh first)" >&2
      exit $EXIT_UNAVAILABLE
    fi
    export ML_PROBE_OUT="$OUT"
    ( cd "$REPO_ROOT" && dotnet test "$PROJECT" -c Release --filter "$FILTER" --logger "trx;LogFileName=probe.trx" \
        --logger "console;verbosity=detailed" --results-directory "$OUT/trx" ) 2>&1 | tail -n 60
    rc=${PIPESTATUS[0]}
    ;;
  --ci)
    if [ "$(uname -s)" != "Linux" ] || [ "$(uname -m)" != "x86_64" ]; then
      echo "[UNAVAILABLE] ml-platform-probe --ci: needs a real Linux x86_64 host, found $(uname -s)/$(uname -m). Not a pass." >&2
      exit $EXIT_UNAVAILABLE
    fi
    if [ ! -e /usr/lib/x86_64-linux-gnu/libiomp5.so ]; then
      echo "[UNAVAILABLE] ml-platform-probe --ci: libiomp5.so (OpenMP, needed by libMklImports.so) is not installed. On Ubuntu: sudo apt-get install -y libomp-dev. Not a pass." >&2
      exit $EXIT_UNAVAILABLE
    fi
    export ML_PROBE_OUT="$OUT"
    ( cd "$REPO_ROOT" && timeout "$TIMEOUT_SECONDS" dotnet test "$PROJECT" -c Release --filter "$FILTER" \
        --logger "trx;LogFileName=probe.trx" --logger "console;verbosity=detailed" --results-directory "$OUT/trx" ) 2>&1 | tail -n 80
    rc=${PIPESTATUS[0]}
    ;;
  --docker-amd64)
    if ! need_tool docker || ! docker info >/dev/null 2>&1; then
      echo "[UNAVAILABLE] ml-platform-probe: docker daemon is not reachable. Not a pass." >&2
      exit $EXIT_UNAVAILABLE
    fi
    # Sources are copied (not built in place) so the host's arm64 bin/obj are never touched or reused. Only global.json, the
    # solution-free project closure of tests/Revenant and its sources are needed.
    # shellcheck disable=SC2016  # the single-quoted body is meant to expand inside the container, not here
    INNER='set -eu
      uname -m; . /etc/os-release; echo "image OS: $PRETTY_NAME"; dotnet --version
      # Microsoft.ML.Mkl.Redist ships libMklImports.so but NOT its OpenMP dependency libiomp5.so. On Ubuntu/Debian the smallest
      # package that provides that name is libomp-dev (symlink libiomp5.so). Installed in this throwaway probe container only.
      apt-get update -qq >/dev/null && apt-get install -y -qq --no-install-recommends libomp-dev >/dev/null
      ls -l /usr/lib/x86_64-linux-gnu/libiomp5.so
      mkdir -p /work && cd /work
      tar -C /src --exclude=bin --exclude=obj --exclude=node_modules --exclude=.git --exclude=TestResults -cf - global.json src tests/Revenant | tar -xf -
      timeout '"$TIMEOUT_SECONDS"' dotnet test '"$PROJECT"' -c Release --filter "'"$FILTER"'" \
        --logger "trx;LogFileName=probe.trx" --logger "console;verbosity=detailed" --results-directory /out/trx'
    docker run --rm --platform linux/amd64 \
      -v "$REPO_ROOT:/src:ro" -v "$OUT:/out" -v "$NUGET_VOLUME:/root/.nuget/packages" \
      -e ML_PROBE_OUT=/out -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
      "$SDK_IMAGE" sh -c "$INNER" 2>&1 | tail -n 80
    rc=${PIPESTATUS[0]}
    ;;
esac

if [ "$rc" -ne 0 ]; then
  echo "[FAILED] ml-platform-probe $MODE: test host exit $rc"
  record FAILED "ml-platform$MODE" "exit $rc"
elif detail="$(trx_check "$OUT/trx" "$EXPECTED")"; then
  echo "$detail"
  echo "[PASSED] ml-platform-probe $MODE: $EXPECTED probe test(s) executed and passed; evidence in $OUT"
  record PASSED "ml-platform$MODE" "$EXPECTED probe test(s)"
else
  echo "$detail"
  echo "[FAILED] ml-platform-probe $MODE: the expected probe tests did not all run and pass"
  record FAILED "ml-platform$MODE" "tests did not all run"
fi
print_summary "ML.NET platform probe $MODE"
exit $?
