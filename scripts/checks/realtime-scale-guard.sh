#!/usr/bin/env bash
# Deployment-config half of the one-Realtime-instance invariant (R03.07, ADR-P1-02, ADR-P1-11).
# Resolves the Compose model exactly as `docker compose` will run it (anchors, override files, !reset)
# and fails when the Realtime host could run as more than one process:
#   - services.realtime missing, or more than one service builds deploy/docker/Realtime.Dockerfile
#   - scale or deploy.replicas other than 1, or deploy.mode "global"
# The runtime guard (RealtimeInstanceGuard advisory lock) covers what config cannot see, such as
# `docker compose up --scale realtime=2`. See docs/architecture/07-realtime-room-authority.md.
#
# Usage: realtime-scale-guard.sh [-f COMPOSE_FILE]... [--env-file FILE]
#   Defaults: -f deploy/compose/docker-compose.yml --env-file .env.example (values are only interpolated,
#   never printed; the resolved model is parsed in memory).
# Exit: 0 single instance, 1 guard violated or the Compose model is invalid, 2 bad arguments, 3 docker/jq missing.
set -uo pipefail
# shellcheck source=scripts/checks/lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

COMPOSE_FILES=()
ENV_FILE=".env.example"
while [ $# -gt 0 ]; do
  case "$1" in
    -f|--file) [ $# -ge 2 ] || { echo "usage: $0 [-f COMPOSE_FILE]... [--env-file FILE]" >&2; exit $EXIT_USAGE; }
      COMPOSE_FILES+=("$2"); shift 2 ;;
    --env-file) [ $# -ge 2 ] || { echo "usage: $0 [-f COMPOSE_FILE]... [--env-file FILE]" >&2; exit $EXIT_USAGE; }
      ENV_FILE="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; echo "usage: $0 [-f COMPOSE_FILE]... [--env-file FILE]" >&2; exit $EXIT_USAGE ;;
  esac
done
[ ${#COMPOSE_FILES[@]} -gt 0 ] || COMPOSE_FILES=("deploy/compose/docker-compose.yml")

for tool in docker jq; do
  if ! need_tool "$tool"; then
    echo "[UNAVAILABLE] realtime-scale-guard: '$tool' is not on PATH; the guard did not run (this is not a pass)" >&2
    exit $EXIT_UNAVAILABLE
  fi
done

ARGS=(compose --env-file "$ENV_FILE")
for f in "${COMPOSE_FILES[@]}"; do ARGS+=(-f "$f"); done
ARGS+=(config --format json)

model="$(mktemp)"
errors="$(mktemp)"
trap 'rm -f "$model" "$errors"' EXIT
if ! ( cd "$REPO_ROOT" && docker "${ARGS[@]}" ) >"$model" 2>"$errors"; then
  echo "[FAILED] realtime-scale-guard: docker compose could not resolve the model:"
  tail -n 5 "$errors"
  record FAILED realtime-replicas "compose model invalid"
  print_summary "realtime scale guard"
  exit $?
fi

# One line per violation; empty output means the invariant holds.
violations="$(jq -r '
  (.services // {}) as $s
  | ([$s | to_entries[] | select(.key == "realtime" or ((.value.build.dockerfile // "") | test("(^|/)Realtime\\.Dockerfile$"))) | .key]) as $rt
  | (if ($s | has("realtime") | not) then ["services.realtime is missing"] else [] end)
  + (if ($rt | length) > 1 then ["more than one Realtime service: \($rt | join(", "))"] else [] end)
  + [ $rt[] as $n | $s[$n] as $v
      | (if ($v.scale != null and $v.scale != 1) then "services.\($n).scale=\($v.scale)" else empty end),
        (if ($v.deploy.replicas? != null and $v.deploy.replicas != 1) then "services.\($n).deploy.replicas=\($v.deploy.replicas)" else empty end),
        (if ($v.deploy.mode? == "global") then "services.\($n).deploy.mode=global" else empty end) ]
  | .[]' "$model")" || { echo "[FAILED] realtime-scale-guard: could not parse the resolved model"; exit $EXIT_FAILED; }

if [ -n "$violations" ]; then
  echo "[FAILED] realtime-scale-guard: Realtime must run as exactly ONE replica (single authoritative room loop owner)."
  while IFS= read -r line; do echo "  - $line"; done <<<"$violations"
  echo "  A Redis backplane is transport only and does not make a second replica safe. Multi-replica Realtime needs an"
  echo "  owner lease/fence ADR first; see docs/architecture/07-realtime-room-authority.md."
  record FAILED realtime-replicas "$(echo "$violations" | tr '\n' ';')"
else
  echo "[PASSED] realtime-scale-guard: services.realtime resolves to exactly one replica"
  record PASSED realtime-replicas "1 replica (${COMPOSE_FILES[*]})"
fi
print_summary "realtime scale guard"
exit $?
