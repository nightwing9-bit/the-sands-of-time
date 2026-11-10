#!/usr/bin/env bash
# Regenerates, or with --check verifies, the typed browser contracts:
#   Hosts.Api (build-time OpenAPI) -> web/packages/contracts/openapi/hosts-api.json
#                                  -> web/packages/contracts/src/generated/{hosts-api,contract-version}.ts
# Usage: scripts/contracts/generate.sh [--check]
#   (default)  rewrite the three committed outputs from the current C# host
#   --check    write nothing; exit 1 with a diff when any committed output is stale
# Needs the .NET SDK required by global.json and `npm ci` already run in web/. No database or Redis is contacted:
# the host is only built and its endpoint metadata read, never started.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
mode="write"
case "${1:-}" in
  "") ;;
  --check) mode="check" ;;
  *) echo "usage: $0 [--check]" >&2; exit 2 ;;
esac

json_rel="web/packages/contracts/openapi/hosts-api.json"
gen_rel="web/packages/contracts/src/generated"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# 1. Emit the OpenAPI document. Fixed dummy configuration keeps the output independent of the caller's environment;
#    dev-seed passwords are cleared so the (never-started) host cannot try to reach a database.
#    The SDK target skips itself while its cache file is newer than the assembly, so remove the cache to always emit.
rm -f "$root/src/Hosts.Api/obj/Hosts.Api.OpenApiFiles.cache"
unset Identity__DevSeed__OperatorPassword Identity__DevSeed__SupervisorPassword Identity__MasterSecret || true
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__Postgres="Host=127.0.0.1;Port=1;Database=contracts;Username=contracts;Password=unused" \
ConnectionStrings__Redis="127.0.0.1:1,abortConnect=false" \
  dotnet build "$root/src/Hosts.Api/Hosts.Api.csproj" --configuration Release \
    -p:EmitOpenApi=true -p:OpenApiOutputDirectory="$work/openapi" -warnaserror --nologo -v:q >"$work/dotnet.log" 2>&1 || {
      cat "$work/dotnet.log" >&2
      echo "contracts: could not build Hosts.Api / emit OpenAPI" >&2
      exit 1
    }
emitted="$work/openapi/Hosts.Api.json"
if [ ! -s "$emitted" ]; then
  cat "$work/dotnet.log" >&2
  echo "contracts: OpenAPI document was not produced" >&2
  exit 1
fi
mkdir -p "$work/out"
cp "$emitted" "$work/out/hosts-api.json"

# 2. Generate the TypeScript from the document just emitted (not from the committed copy).
(cd "$root/web" && node packages/contracts/scripts/generate-ts.mjs "$work/out/hosts-api.json" "$work/out/generated")

if [ "$mode" = "write" ]; then
  mkdir -p "$root/web/packages/contracts/openapi" "$root/$gen_rel"
  cp "$work/out/hosts-api.json" "$root/$json_rel"
  cp "$work/out/generated/hosts-api.ts" "$work/out/generated/contract-version.ts" "$root/$gen_rel/"
  echo "contracts: regenerated $json_rel and $gen_rel/{hosts-api,contract-version}.ts"
  exit 0
fi

stale=0
compare() { # <fresh> <committed>
  if [ ! -f "$2" ]; then
    echo "contracts: STALE - missing committed file ${2#"$root"/}" >&2
    stale=1
  elif ! diff -u --label "committed:${2#"$root"/}" --label "generated" "$2" "$1" >&2; then
    stale=1
  fi
}
compare "$work/out/hosts-api.json" "$root/$json_rel"
compare "$work/out/generated/hosts-api.ts" "$root/$gen_rel/hosts-api.ts"
compare "$work/out/generated/contract-version.ts" "$root/$gen_rel/contract-version.ts"
if [ "$stale" -ne 0 ]; then
  echo "contracts: FAILED - generated client output is stale. Run scripts/contracts/generate.sh and commit the result." >&2
  exit 1
fi
echo "contracts: PASSED - committed OpenAPI document and generated TypeScript match the C# host"
