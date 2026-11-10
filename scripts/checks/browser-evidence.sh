#!/usr/bin/env bash
# Browser Evidence Capture: runs ONE named, existing Playwright route spec and keeps its artifacts.
# If Playwright, the E2E config or the route spec is missing it reports NOT PROVISIONED; it never reports a pass
# without having run a browser test. Ignores the hook event JSON on stdin. Never invokes Kiro.
#
# Usage: bash scripts/checks/browser-evidence.sh [--route NAME] [--required] [--gate-file PATH]
#   route: --route NAME, else `browser_route=NAME` in the gate marker (scripts/checks/.active-gate); NAME is [a-z0-9-]{1,64}
#   spec file: tests/E2E/routes/NAME.spec.ts; config: tests/E2E/playwright.config.ts
#   playwright binary: tests/E2E/node_modules/.bin/playwright or web/node_modules/.bin/playwright (never downloaded)
#   --required (or BROWSER_EVIDENCE_REQUIRED=1): treat "cannot run" as failure.
# Artifacts: docs/evidence/browser/NAME/ (only written when a test actually ran).
# Exit: 0 test passed, or not required and not provisioned/no route (reported as SKIPPED);
#       1 test ran and failed; 2 bad arguments or route name; 3 required but not provisioned.

set -u
# shellcheck source=scripts/checks/lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

REQUIRED="${BROWSER_EVIDENCE_REQUIRED:-0}"
GATE_FILE="$CHECKS_DIR/.active-gate"
ROUTE=""
while [ $# -gt 0 ]; do
  case "$1" in
    --required) REQUIRED=1 ;;
    --route)
      [ $# -ge 2 ] || { echo "browser-evidence: --route needs a name" >&2; exit $EXIT_USAGE; }
      ROUTE="$2"; shift ;;
    --gate-file)
      [ $# -ge 2 ] || { echo "browser-evidence: --gate-file needs a path" >&2; exit $EXIT_USAGE; }
      GATE_FILE="$2"; shift ;;
    *) echo "browser-evidence: unknown argument '$1'" >&2; exit $EXIT_USAGE ;;
  esac
  shift
done

if [ -z "$ROUTE" ] && [ -f "$GATE_FILE" ]; then
  ROUTE="$(sed -n 's/^browser_route=\([A-Za-z0-9_.,:-]*\)\r\{0,1\}$/\1/p' "$GATE_FILE" | tail -n 1)"
fi

not_provisioned() { # reason
  if [ "$REQUIRED" = "1" ]; then
    echo "[FAILED] browser-evidence: NOT PROVISIONED (required gate): $1" >&2
    exit $EXIT_UNAVAILABLE
  fi
  skip_step "browser-evidence" "NOT PROVISIONED: $1"
  print_summary "browser-evidence"
  exit $?
}

if [ -z "$ROUTE" ]; then
  not_provisioned "no route named (use --route NAME or browser_route= in the gate marker)"
fi
if ! [[ "$ROUTE" =~ ^[a-z0-9][a-z0-9-]{0,63}$ ]]; then
  echo "browser-evidence: invalid route name '$ROUTE' (allowed: [a-z0-9-], max 64, no leading dash)" >&2
  exit $EXIT_USAGE
fi

SPEC="tests/E2E/routes/$ROUTE.spec.ts"
CONFIG="tests/E2E/playwright.config.ts"
PW=""
for c in "$REPO_ROOT/tests/E2E/node_modules/.bin/playwright" "$REPO_ROOT/web/node_modules/.bin/playwright"; do
  [ -x "$c" ] && { PW="$c"; break; }
done

[ -n "$PW" ] || not_provisioned "Playwright is not installed (route '$ROUTE')"
[ -f "$REPO_ROOT/$CONFIG" ] || not_provisioned "$CONFIG does not exist (route '$ROUTE')"
[ -f "$REPO_ROOT/$SPEC" ] || not_provisioned "route spec $SPEC does not exist"

OUT="docs/evidence/browser/$ROUTE"
mkdir -p "$REPO_ROOT/$OUT"
run_step "browser:$ROUTE" "$PW" test "$SPEC" --config "$CONFIG" --output "$OUT"
print_summary "browser-evidence"
exit $?
