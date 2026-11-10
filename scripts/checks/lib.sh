#!/usr/bin/env bash
# shellcheck disable=SC2034  # exit-code constants are consumed by the sourcing scripts
# Shared helpers for scripts/checks/*.sh. Source this file; do not execute it.
# Contract: every step ends as exactly one of PASSED, FAILED or SKIPPED. A step is
# PASSED only when its command ran and exited 0. Nothing here invokes Kiro or any agent.

CHECKS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$CHECKS_DIR/../.." && pwd)"

# Exit codes shared by all check scripts (used by the sourcing scripts).
EXIT_OK=0
EXIT_FAILED=1        # a check that ran failed
EXIT_USAGE=2         # bad arguments or malformed gate marker; nothing was run
EXIT_UNAVAILABLE=3   # a required check could not run (strict / required mode)

CHECKS_STRICT="${CHECKS_STRICT:-0}"
N_PASSED=0
N_FAILED=0
N_SKIPPED=0
RESULT_LINES=()

record() { # status name detail
  RESULT_LINES+=("$(printf '%-8s %-22s %s' "$1" "$2" "$3")")
  case "$1" in
    PASSED) N_PASSED=$((N_PASSED + 1)) ;;
    FAILED) N_FAILED=$((N_FAILED + 1)) ;;
    SKIPPED) N_SKIPPED=$((N_SKIPPED + 1)) ;;
  esac
}

skip_step() { # name reason
  echo "[SKIPPED] $1: $2"
  record SKIPPED "$1" "$2"
}

# run_step NAME COMMAND [ARGS...]: runs in the repo root, captures output, prints the
# tail on failure. Test-runner totals (e.g. "Passed!  - Failed: 0, Passed: 10") become the detail.
run_step() {
  local name="$1"; shift
  local log rc detail
  log="$(mktemp)"
  echo "[RUN] $name: $*"
  ( cd "$REPO_ROOT" && "$@" ) >"$log" 2>&1
  rc=$?
  detail="$(grep -E '(Passed|Failed)!|error TS[0-9]+|Build succeeded|Time Elapsed' "$log" | tail -n 2 | tr '\n' ' ')"
  if [ "$rc" -eq 0 ]; then
    echo "[PASSED] $name ${detail}"
    record PASSED "$name" "${detail:-exit 0}"
  else
    echo "[FAILED] $name (exit $rc)"
    tail -n 40 "$log"
    record FAILED "$name" "exit $rc ${detail}"
  fi
  rm -f "$log"
  return 0
}

need_tool() { # tool -> 0 if on PATH
  command -v "$1" >/dev/null 2>&1
}

# Reject a non-zero exit from tools that would otherwise rewrite tracked files.
worktree_fingerprint() {
  if need_tool git && git -C "$REPO_ROOT" rev-parse --git-dir >/dev/null 2>&1; then
    { git -C "$REPO_ROOT" diff --no-ext-diff; git -C "$REPO_ROOT" ls-files --others --exclude-standard; } | cksum
  else
    echo "no-git"
  fi
}

# print_summary TITLE; returns the exit code for the script.
print_summary() {
  echo
  echo "== $1 =="
  local l
  for l in "${RESULT_LINES[@]:-}"; do [ -n "$l" ] && echo "$l"; done
  if [ "$N_FAILED" -gt 0 ]; then
    echo "RESULT: FAILED ($N_FAILED failed, $N_PASSED passed, $N_SKIPPED skipped)"
    return $EXIT_FAILED
  fi
  if [ "$N_PASSED" -eq 0 ]; then
    echo "RESULT: NOTHING VERIFIED (all $N_SKIPPED step(s) skipped; this is not a pass)"
  else
    if [ "$N_SKIPPED" -gt 0 ]; then
      echo "RESULT: OK ($N_PASSED passed, $N_SKIPPED skipped; skipped steps were not verified)"
    else
      echo "RESULT: OK ($N_PASSED passed, 0 skipped)"
    fi
  fi
  if [ "$N_SKIPPED" -gt 0 ] && [ "$CHECKS_STRICT" = "1" ]; then
    echo "STRICT: skipped steps are treated as failure"
    return $EXIT_UNAVAILABLE
  fi
  return $EXIT_OK
}
