#!/usr/bin/env bash
# Architecture & Quality Gate: runs only the allow-listed fast suites named by the active task gate marker.
# The marker is parsed as data (never sourced). Ignores the hook event JSON on stdin. Never invokes Kiro.
# Integration (Testcontainers) and E2E suites are not allow-listed; they belong to separate CI jobs.
#
# Usage: bash scripts/checks/spec-gate.sh [--gate-file PATH] [--strict]
#   default marker: scripts/checks/.active-gate (gitignored; copy from .active-gate.sample)
#   --strict (or CHECKS_STRICT=1): exit 3 if any suite was SKIPPED.
# Exit: 0 ok or no marker (reported as SKIPPED), 1 a suite failed, 2 malformed marker / bad arguments
#       (nothing was run), 3 skipped under --strict.

set -u
# shellcheck source=scripts/checks/lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

GATE_FILE="$CHECKS_DIR/.active-gate"
while [ $# -gt 0 ]; do
  case "$1" in
    --strict) CHECKS_STRICT=1 ;;
    --gate-file)
      [ $# -ge 2 ] || { echo "spec-gate: --gate-file needs a path" >&2; exit $EXIT_USAGE; }
      GATE_FILE="$2"; shift ;;
    *) echo "spec-gate: unknown argument '$1'" >&2; exit $EXIT_USAGE ;;
  esac
  shift
done

ALLOWED_SUITES="dotnet-build architecture web-typecheck unit-marrow unit-revenant unit-ghostlap"

if [ ! -f "$GATE_FILE" ]; then
  skip_step "spec-gate" "no active gate marker at ${GATE_FILE#"$REPO_ROOT"/}; no suite selected"
  print_summary "spec-gate"
  exit $?
fi

# Parse marker strictly before running anything.
SUITES=""
spec_label="" ; task_label=""
lineno=0
while IFS= read -r line || [ -n "$line" ]; do
  lineno=$((lineno + 1))
  line="${line%$'\r'}"
  case "$line" in ''|'#'*) continue ;; esac
  if ! [[ "$line" =~ ^([a-z_]+)=([A-Za-z0-9_.,:-]*)$ ]]; then
    echo "spec-gate: malformed marker line $lineno (expected key=value with [A-Za-z0-9_.,:-] values)" >&2
    exit $EXIT_USAGE
  fi
  key="${BASH_REMATCH[1]}"; val="${BASH_REMATCH[2]}"
  case "$key" in
    spec) spec_label="$val" ;;
    task) task_label="$val" ;;
    suites) SUITES="$val" ;;
    browser_route) : ;; # consumed by browser-evidence.sh only
    *) echo "spec-gate: unknown marker key '$key' at line $lineno" >&2; exit $EXIT_USAGE ;;
  esac
done <"$GATE_FILE"

IFS=',' read -r -a SUITE_LIST <<<"$SUITES"
for s in "${SUITE_LIST[@]:-}"; do
  [ -z "$s" ] && continue
  case " $ALLOWED_SUITES " in
    *" $s "*) ;;
    *) echo "spec-gate: suite '$s' is not allow-listed (allowed: $ALLOWED_SUITES)" >&2; exit $EXIT_USAGE ;;
  esac
done

echo "spec-gate: spec=${spec_label:-?} task=${task_label:-?} suites=${SUITES:-<none>}"
if [ -z "$SUITES" ]; then
  skip_step "spec-gate" "marker names no suites"
  print_summary "spec-gate"
  exit $?
fi

run_test_dir() { # suite name, tests dir (relative)
  local name="$1" dir="$2" proj
  proj="$(find "$REPO_ROOT/$dir" -maxdepth 2 -name '*.csproj' 2>/dev/null | sort | head -n 1)"
  if [ -z "$proj" ]; then
    skip_step "$name" "no test project under $dir"
  else
    run_step "$name" dotnet test "${proj#"$REPO_ROOT"/}" --nologo -v minimal --filter "Category!=Integration&Category!=E2E&Category!=MlSsaX64&Category!=OfflineTraining"
  fi
}

for s in "${SUITE_LIST[@]}"; do
  [ -z "$s" ] && continue
  case "$s" in
    dotnet-build|architecture|unit-marrow|unit-revenant|unit-ghostlap)
      if ! need_tool dotnet; then skip_step "$s" "dotnet SDK not on PATH"; continue; fi ;;
    web-typecheck)
      if ! need_tool npm || [ ! -d "$REPO_ROOT/web/node_modules" ]; then
        skip_step "$s" "npm missing or web/node_modules absent (run: npm --prefix web ci)"; continue
      fi ;;
  esac
  case "$s" in
    dotnet-build)  run_step "$s" dotnet build TheSandsOfTime.slnx --nologo -v minimal ;;
    architecture)
      if [ -f "$REPO_ROOT/tests/Architecture/Architecture.Tests.csproj" ]; then
        run_step "$s" dotnet test tests/Architecture/Architecture.Tests.csproj --nologo -v minimal
      else
        skip_step "$s" "tests/Architecture/Architecture.Tests.csproj does not exist"
      fi ;;
    web-typecheck) run_step "$s" npm --prefix web run typecheck ;;
    unit-marrow)   run_test_dir "$s" tests/Marrow ;;
    unit-revenant) run_test_dir "$s" tests/Revenant ;;
    unit-ghostlap) run_test_dir "$s" tests/GhostLap ;;
  esac
done

print_summary "spec-gate"
exit $?
