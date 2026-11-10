#!/usr/bin/env bash
# Marrow Contract Guardian: fast compile + type + architecture check for public contract changes.
# Read-only with respect to source: builds into gitignored bin/obj and fails if the working
# tree fingerprint changed while it ran. Ignores the hook event JSON on stdin. Never invokes Kiro.
#
# Usage: bash scripts/checks/contract-guardian.sh [--strict]
#   --strict (or CHECKS_STRICT=1): exit 3 if any step had to be SKIPPED.
# Exit: 0 ok (skips are reported), 1 a check failed, 3 skipped under --strict.

set -u
# shellcheck source=scripts/checks/lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

for arg in "$@"; do
  case "$arg" in
    --strict) CHECKS_STRICT=1 ;;
    *) echo "contract-guardian: unknown argument '$arg'" >&2; exit $EXIT_USAGE ;;
  esac
done

before="$(worktree_fingerprint)"

# 1. Public contract assemblies must compile.
CONTRACT_PROJECTS=(
  src/Marrow.Kernel/Marrow.Kernel.csproj
  src/Marrow.Timeline/Marrow.Timeline.csproj
  src/Revenant.Contracts/Revenant.Contracts.csproj
  src/GhostLap.Contracts/GhostLap.Contracts.csproj
)
if need_tool dotnet; then
  for p in "${CONTRACT_PROJECTS[@]}"; do
    if [ -f "$REPO_ROOT/$p" ]; then
      run_step "build:$(basename "$p" .csproj)" dotnet build "$p" --nologo -v minimal
    else
      skip_step "build:$(basename "$p" .csproj)" "project file $p does not exist"
    fi
  done
  # 2. Compiled-assembly boundary rules (NetArchTest).
  if [ -f "$REPO_ROOT/tests/Architecture/Architecture.Tests.csproj" ]; then
    run_step "architecture-tests" dotnet test tests/Architecture/Architecture.Tests.csproj --nologo -v minimal
  else
    skip_step "architecture-tests" "tests/Architecture/Architecture.Tests.csproj does not exist"
  fi
else
  skip_step "dotnet-build+architecture" "dotnet SDK not on PATH"
fi

# 3. Shared TypeScript contract packages must type-check.
if need_tool npm && [ -d "$REPO_ROOT/web/node_modules" ]; then
  run_step "web-contract-types" npm --prefix web run typecheck --workspace @sands/contracts --workspace @sands/timeline-ui
else
  skip_step "web-contract-types" "npm missing or web/node_modules absent (run: npm --prefix web ci)"
fi

# 4. Prove the guardian did not modify the working tree.
after="$(worktree_fingerprint)"
if [ "$before" = "$after" ]; then
  record PASSED "worktree-unchanged" "fingerprint identical before and after"
else
  echo "[FAILED] worktree changed while the guardian ran (files may have been edited concurrently or by a tool)"
  record FAILED "worktree-unchanged" "fingerprint differs"
fi

print_summary "contract-guardian"
exit $?
