#!/usr/bin/env bash
# Task 10.5 multi-room benchmark over real WebSockets to a separate Kestrel Realtime process.
# Plays ROOMS rooms of PLAYERS competitors and SPECTATORS spectators at once, each competitor steering from authoritative snapshots at 30 Hz,
# and records client traffic and result evidence in OUT/summary.json, with host measurements in OUT/host-profile.jsonl.
# --enforce checks the automated performance targets on a full round. See docs/architecture/ghostlap-mp-load-benchmark.md.
#
# Usage: ghostlap-load-benchmark.sh [--rooms N] [--players 2-4] [--spectators N] [--ticks N] [--out DIR] [--configuration Release|Debug] [--enforce]
#   Defaults: 4 rooms, 4 players, 2 spectators per room, 2700 ticks (a full 90 s round), out TestResults/ghostlap-load/<UTC stamp>, Release.
#   --smoke is shorthand for --rooms 1 --ticks 150 --configuration Debug (a 5 s instrumentation check, not a capacity measurement).
# Exit: 0 ran and wrote the files, 1 the test failed (including --enforce target misses), 2 bad arguments, 3 docker or dotnet missing.
set -uo pipefail
# shellcheck source=scripts/checks/lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
ROOMS=4; PLAYERS=4; SPECTATORS=2; TICKS=2700; CONFIG=Release; ENFORCE=0; OUT=""
usage() { echo "usage: $0 [--rooms N] [--players 2-4] [--spectators N] [--ticks N] [--out DIR] [--configuration Release|Debug] [--enforce] [--smoke]" >&2; }
num() { [[ "$2" =~ ^[0-9]+$ ]] && [ "$2" -ge 1 ] || { echo "$1 needs a positive integer" >&2; usage; exit $EXIT_USAGE; }; }
while [ $# -gt 0 ]; do
  case "$1" in
    --rooms) [ $# -ge 2 ] || { usage; exit $EXIT_USAGE; }; num --rooms "$2"; ROOMS="$2"; shift 2 ;;
    --players) [ $# -ge 2 ] || { usage; exit $EXIT_USAGE; }; num --players "$2"; PLAYERS="$2"; shift 2 ;;
    --spectators) [ $# -ge 2 ] || { usage; exit $EXIT_USAGE; }; num --spectators "$2"; SPECTATORS="$2"; shift 2 ;;
    --ticks) [ $# -ge 2 ] || { usage; exit $EXIT_USAGE; }; num --ticks "$2"; TICKS="$2"; shift 2 ;;
    --out) [ $# -ge 2 ] || { usage; exit $EXIT_USAGE; }; OUT="$2"; shift 2 ;;
    --configuration) [ $# -ge 2 ] || { usage; exit $EXIT_USAGE; }; CONFIG="$2"; shift 2 ;;
    --enforce) ENFORCE=1; shift ;;
    --smoke) ROOMS=1; TICKS=150; CONFIG=Debug; shift ;;
    *) echo "unknown argument: $1" >&2; usage; exit $EXIT_USAGE ;;
  esac
done
case "$CONFIG" in Release|Debug) ;; *) echo "--configuration must be Release or Debug" >&2; exit $EXIT_USAGE ;; esac
[ "$PLAYERS" -ge 2 ] && [ "$PLAYERS" -le 4 ] || { echo "--players must be 2 to 4" >&2; exit $EXIT_USAGE; }
[ "$SPECTATORS" -le 64 ] || { echo "--spectators must be at most 64" >&2; exit $EXIT_USAGE; }
[ "$TICKS" -le 2700 ] || { echo "--ticks must be at most 2700 (one round)" >&2; exit $EXIT_USAGE; }
# shellcheck source=scripts/checks/dotnet-env.sh
[ -f "$CHECKS_DIR/dotnet-env.sh" ] && [ -x "${DOTNET_SDK_DIR:-$HOME/.dotnet-10.0.403}/dotnet" ] && . "$CHECKS_DIR/dotnet-env.sh" >/dev/null
for tool in dotnet docker; do
  if ! need_tool "$tool"; then
    echo "[UNAVAILABLE] ghostlap-load-benchmark: '$tool' is not on PATH; nothing ran (this is not a pass)" >&2
    exit $EXIT_UNAVAILABLE
  fi
done
docker info >/dev/null 2>&1 || { echo "[UNAVAILABLE] ghostlap-load-benchmark: the Docker daemon is not reachable (Testcontainers PostgreSQL); nothing ran" >&2; exit $EXIT_UNAVAILABLE; }
[ -n "$OUT" ] || OUT="$REPO_ROOT/TestResults/ghostlap-load/$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
rm -f "$OUT/summary.json" || exit $EXIT_FAILED
{
  echo "utc: $(date -u +%FT%TZ)"
  echo "git: $(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo none) dirty=$(git -C "$REPO_ROOT" status --porcelain 2>/dev/null | wc -l | tr -d ' ') file(s)"
  echo "uname: $(uname -a)"
  echo "dotnet: $(dotnet --version)"
  echo "cpu: $(sysctl -n machdep.cpu.brand_string 2>/dev/null || grep -m1 'model name' /proc/cpuinfo 2>/dev/null || echo unknown)"
  echo "logical cores: $(sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo unknown)"
  echo "memory bytes: $(sysctl -n hw.memsize 2>/dev/null || awk '/MemTotal/ {print $2*1024}' /proc/meminfo 2>/dev/null || echo unknown)"
  echo "docker: $(docker info --format '{{.ServerVersion}} {{.OperatingSystem}} {{.Architecture}}' 2>/dev/null)"
  echo "parameters: rooms=$ROOMS players=$PLAYERS spectators_per_room=$SPECTATORS ticks=$TICKS configuration=$CONFIG enforce=$ENFORCE"
} > "$OUT/environment.txt"
cat "$OUT/environment.txt"
export GHOSTLAP_LOAD_PROFILE=1 GHOSTLAP_LOAD_ROOMS="$ROOMS" GHOSTLAP_LOAD_PLAYERS="$PLAYERS" GHOSTLAP_LOAD_SPECTATORS_PER_ROOM="$SPECTATORS" \
       GHOSTLAP_LOAD_TICKS="$TICKS" GHOSTLAP_LOAD_OUT="$OUT" GHOSTLAP_LOAD_ENFORCE="$ENFORCE"
cd "$REPO_ROOT" || exit $EXIT_FAILED
dotnet build src/Hosts.Realtime/Hosts.Realtime.csproj -c "$CONFIG" --nologo > "$OUT/host-build.txt" 2>&1 || {
  cat "$OUT/host-build.txt" >&2
  exit $EXIT_FAILED
}
export GHOSTLAP_LOAD_HOST_DLL="$REPO_ROOT/src/Hosts.Realtime/bin/$CONFIG/net10.0/Hosts.Realtime.dll"
dotnet test tests/Integration/Integration.Tests.csproj -c "$CONFIG" --nologo \
  --filter "FullyQualifiedName~GhostLapLoadProfileTests" \
  --logger "console;verbosity=detailed" --results-directory "$OUT/trx" --logger "trx;LogFileName=load-profile.trx" 2>&1 | tee "$OUT/test-output.txt"
rc=${PIPESTATUS[0]}
if [ "$rc" -ne 0 ]; then
  echo "[FAILED] ghostlap-load-benchmark (exit $rc); evidence so far is in $OUT" >&2
  exit $EXIT_FAILED
fi
if [ ! -s "$OUT/summary.json" ]; then
  echo "[FAILED] ghostlap-load-benchmark: the test passed but wrote no summary.json (it was skipped?); this is not a measurement" >&2
  exit $EXIT_FAILED
fi
echo "[MEASURED] $OUT/summary.json and $OUT/host-profile.jsonl (a measurement, not a verdict; see docs/architecture/ghostlap-mp-load-benchmark.md)"
exit $EXIT_OK
