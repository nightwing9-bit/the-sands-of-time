#!/bin/sh
# One-shot database migration executor (Spec 01). The ONLY thing that applies migrations.
#
# Invoked by the Compose `migrate` job (before any host starts), by CI, and by hand with the same
# command. API/Realtime/Worker never run migrations.
#
# Layout (migrations are supplied by their owning specs; Spec 01 supplies none):
#   scripts/db/migrations/<module>/NNNN_description.sql
# Stable module order (each module owns exactly one schema and its own history table):
#   1 marrow_timeline (Spec 02)   2 marrow_identity (Spec 03)   3 revenant   4 ghostlap
# Within a module, files apply in ascending filename order. Forward-only: an applied file whose
# checksum later changes is an error. Each file runs in ONE transaction together with its history
# row, so a failure leaves nothing half-applied. Do not put BEGIN/COMMIT or CONCURRENTLY statements
# in a migration. Cross-schema foreign keys are forbidden (owners must not reference another schema).
#
# Environment (standard libpq variables; there are no defaults for credentials):
#   PGHOST PGUSER PGPASSWORD PGDATABASE   required;   PGPORT (default 5432)
#   MIGRATIONS_DIR                         default: <this dir>/migrations
#   MIGRATE_CONNECT_ATTEMPTS               default 30 (2 s apart); connection wait is bounded
# Exit status: 0 only if every pending migration applied; nonzero (with a message) otherwise.

set -eu
LC_ALL=C
export LC_ALL

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
MIGRATIONS_DIR=${MIGRATIONS_DIR:-$SCRIPT_DIR/migrations}
MODULES="marrow_timeline marrow_identity revenant ghostlap"
ATTEMPTS=${MIGRATE_CONNECT_ATTEMPTS:-30}

log() { printf 'migrate: %s\n' "$*"; }
fail() { printf 'migrate: ERROR: %s\n' "$*" >&2; exit 1; }
trap 'rc=$?; if [ "$rc" -ne 0 ]; then printf "migrate: FAILED (exit %s); nothing further was applied\n" "$rc" >&2; fi' EXIT

for var in PGHOST PGUSER PGPASSWORD PGDATABASE; do
  eval "value=\${$var:-}"
  [ -n "$value" ] || fail "$var is required (see .env.example); there is no default"
done
case "$PGPASSWORD" in REPLACE_ME*) fail "PGPASSWORD is still the .env.example placeholder" ;; esac
export PGPORT="${PGPORT:-5432}"
[ -d "$MIGRATIONS_DIR" ] || fail "MIGRATIONS_DIR '$MIGRATIONS_DIR' does not exist"

sql() { psql -X -q -At -v ON_ERROR_STOP=1 "$@"; }

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
  else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# Reject directories that are not a known module (typo protection; a stray folder is never ignored).
for entry in "$MIGRATIONS_DIR"/*; do
  [ -d "$entry" ] || continue
  name=$(basename "$entry")
  case " $MODULES " in *" $name "*) ;; *) fail "unknown migration module directory '$name' (allowed: $MODULES)" ;; esac
done

# Bounded wait for the database.
attempt=1
until sql -c 'SELECT 1' >/dev/null 2>&1; do
  if [ "$attempt" -ge "$ATTEMPTS" ]; then
    sql -c 'SELECT 1' >/dev/null || true # show the last connection error once
    fail "database $PGHOST:$PGPORT not reachable after $ATTEMPTS attempts"
  fi
  attempt=$((attempt + 1))
  sleep 2
done

applied=0
skipped=0
for module in $MODULES; do
  dir="$MIGRATIONS_DIR/$module"
  found=0
  for file in "$dir"/*; do
    [ -f "$file" ] || continue
    found=1
    base=$(basename "$file")
    case "$base" in
      [0-9][0-9][0-9][0-9]_*.sql) ;;
      *) fail "$module/$base does not match NNNN_description.sql" ;;
    esac
    version=${base%.sql}
    case "$version" in *[!a-z0-9_]*) fail "$module/$base: only lowercase letters, digits and underscores are allowed" ;; esac
    case "$file" in *\'*) fail "$module/$base: path must not contain a single quote" ;; esac
  done
  if [ "$found" -eq 0 ]; then
    log "module $module: no migrations supplied yet"
    continue
  fi

  # Module-owned schema and its own migration history.
  sql -c "CREATE SCHEMA IF NOT EXISTS $module" \
      -c "CREATE TABLE IF NOT EXISTS $module.schema_migrations (version text PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())"

  for file in "$dir"/*.sql; do
    version=$(basename "$file" .sql)
    checksum=$(sha256 "$file")
    previous=$(sql -c "SELECT checksum FROM $module.schema_migrations WHERE version = '$version'")
    if [ -n "$previous" ]; then
      [ "$previous" = "$checksum" ] || fail "$module/$version was modified after it was applied (applied $previous, now $checksum); migrations are forward-only"
      skipped=$((skipped + 1))
      continue
    fi
    log "module $module: applying $version"
    {
      printf 'BEGIN;\n'
      printf "SELECT pg_advisory_xact_lock(hashtext('the-sands-of-time:migrate'));\n"
      printf "\\\\i '%s'\n" "$file"
      printf "INSERT INTO %s.schema_migrations (version, checksum) VALUES ('%s', '%s');\n" "$module" "$version" "$checksum"
      printf 'COMMIT;\n'
    } | sql >/dev/null || fail "$module/$version failed and was rolled back"
    applied=$((applied + 1))
  done
done

log "done: $applied applied, $skipped already applied"
