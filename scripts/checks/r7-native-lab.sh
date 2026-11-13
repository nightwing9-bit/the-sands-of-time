#!/usr/bin/env bash
# R7/2 native research lab: runs selected Revenant tests on Linux AMD64 inside Docker (Rosetta or QEMU on Apple silicon,
# native on x86_64). This is a development convenience for bounded experiments, not the authoritative CI gate.
#
#   scripts/checks/r7-native-lab.sh '<dotnet test filter>' [VAR=value ...]
#
# Sources are copied into a persistent Docker volume so incremental builds are fast and the host bin/obj are never reused.
# The repository's TestResults directory is mounted read-write at /repo/TestResults; pass paths under it to the tests.
# PostgreSQL tests reach the host Docker daemon through its socket (Testcontainers), so they can run here too.
set -euo pipefail
REPO_ROOT=$(cd "$(dirname "$0")/../.." && pwd)
FILTER=${1:?usage: r7-native-lab.sh '<filter>' [VAR=value ...]}
shift
IMAGE=${R7_LAB_IMAGE:-sands-r7-native:local}
WORK=${R7_LAB_WORK_VOLUME:-sands-r7-lab-work}
NUGET=${R7_LAB_NUGET_VOLUME:-sands-ml-probe-nuget}

if ! docker image inspect "$IMAGE" >/dev/null 2>&1; then
  tmp=$(mktemp -d)
  printf 'FROM mcr.microsoft.com/dotnet/sdk:10.0\nRUN apt-get update -qq && apt-get install -y -qq --no-install-recommends libomp-dev >/dev/null && rm -rf /var/lib/apt/lists/*\n' > "$tmp/Dockerfile"
  docker build --platform linux/amd64 -t "$IMAGE" "$tmp"
fi

envs=(-e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e TESTCONTAINERS_RYUK_DISABLED=true
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal)
for kv in "$@"; do envs+=(-e "$kv"); done

# shellcheck disable=SC2016
INNER='set -eu
  mkdir -p /work/src && cd /work/src
  tar -C /repo --exclude=bin --exclude=obj --exclude=node_modules --exclude=.git --exclude=TestResults -cf - global.json TheSandsOfTime.slnx scripts/db src tests/Revenant | tar -xf -
  dotnet test tests/Revenant/Revenant.Tests.csproj -c Release --filter "$R7_FILTER" \
    --logger "trx;LogFileName=r7-lab.trx" --logger "console;verbosity=normal" --results-directory /repo/TestResults/r7-recovery/lab-trx'
docker run --rm --platform linux/amd64 \
  -v "$REPO_ROOT:/repo:ro" -v "$REPO_ROOT/TestResults:/repo/TestResults" \
  -v "$WORK:/work" -v "$NUGET:/root/.nuget/packages" -v /var/run/docker.sock:/var/run/docker.sock \
  --add-host=host.docker.internal:host-gateway \
  -e R7_FILTER="$FILTER" "${envs[@]}" "$IMAGE" sh -c "$INNER"
