#!/usr/bin/env bash
# Disposable PostgreSQL 18 test harness for issue #734.
#
# Purpose: qualify the PostgreSQL-15-pinned CI against the production
# PostgreSQL 18 major, provider-free, without editing
# .github/workflows/ci.yml (owned elsewhere) and without ever touching a real
# database.
#
# Safe defaults:
#   * Nothing runs unless the opt-in classifier allows the context.
#   * Every database the lane touches must carry the disposable taslim_pg18_
#     prefix and sit on a loopback/service host, so an inherited production
#     URL fails the run instead of migrating a real database.
#   * The migration ladder and the test run use SEPARATE databases. The
#     existing api job lets test classes call EnsureCreated() on an empty
#     database; reusing that same database for `dotnet ef database update`
#     makes EnsureCreated() a no-op and hides the newest tables, which
#     reported four false PG18 failures on the first local run.
#   * Provider-free by construction: Ai__OpenAI__Enabled and
#     MovieVideo__Enabled are forced off for the whole run; nothing here
#     deploys, merges or charges.
#   * The expected major is asserted on both live databases: the migration
#     ladder before migration and the reset test database before each suite.
#     A PG18 ladder cannot mask a PG15 test server in an opt-in local run.
#
# Modes (first argument):
#   autopilot           bounded Autopilot regression (leases, takeover,
#                       idempotent events) + EF migration ladder on PG18
#   billing             billing/ledger/credit regression on PG18
#   autopilot-control   identical Autopilot filter against the PG15 control
#   billing-control     identical billing filter against the PG15 control
#   schema-diff         the identical migration ladder applied to PG15 and
#                       PG18, then diffed at column level
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"
# shellcheck source=scripts/lib/pg18-parity-refs.sh
. "$ROOT_DIR/scripts/lib/pg18-parity-refs.sh"

if ! command -v dotnet >/dev/null 2>&1 && [[ -x "$HOME/.dotnet/dotnet" ]]; then
  export PATH="$HOME/.dotnet:$PATH"
fi

usage() {
  printf 'Usage: %s <autopilot|billing|autopilot-control|billing-control|schema-diff>\n' "$0" >&2
}

mode="${1:-}"
[[ -n "$mode" ]] || { usage; exit 2; }

fail() { printf 'PG18 parity: %s\n' "$*" >&2; exit 1; }
step() { printf '\n==> %s\n' "$1"; }

# Provider-free by construction, whatever the caller's environment says.
export Ai__OpenAI__Enabled=false
export MovieVideo__Enabled=false
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"

# --- mode table -------------------------------------------------------------
# expected major, primary URL env vars (in priority order), ladder suffix,
# dotnet test filter, artifact name.
case "$mode" in
  autopilot)
    expected_major=18; urls=(PG18_TEST_URL FRESH_DATABASE_URL ConnectionStrings__Postgres)
    filter="FullyQualifiedName~Autopilot"; artifact=/tmp/pg18-autopilot.log ;;
  billing)
    expected_major=18; urls=(PG18_TEST_URL ConnectionStrings__Postgres)
    filter="FullyQualifiedName~Billing|FullyQualifiedName~Usage|FullyQualifiedName~Credit"
    artifact=/tmp/pg18-billing.log ;;
  autopilot-control)
    expected_major=15; urls=(PG15_TEST_URL PG15_URL)
    filter="FullyQualifiedName~Autopilot"; artifact=/tmp/pg15-autopilot-control.log ;;
  billing-control)
    expected_major=15; urls=(PG15_TEST_URL PG15_URL)
    filter="FullyQualifiedName~Billing|FullyQualifiedName~Usage|FullyQualifiedName~Credit"
    artifact=/tmp/pg15-billing-control.log ;;
  schema-diff)
    expected_major=18; urls=(PG18_URL); filter=""; artifact="" ;;
  *) usage; fail "unknown mode '$mode'" ;;
esac

step "Opt-in context"
CURRENT_BRANCH="$(git branch --show-current || true)"
context="$(pg18_classify_ref \
  "${GITHUB_EVENT_NAME:-}" \
  "${GITHUB_REF:-}" \
  "${GITHUB_BASE_REF:-}" \
  "${GITHUB_HEAD_REF:-}" \
  "$CURRENT_BRANCH")"
printf '  lane context: %s\n' "$context"

# --- connection helpers -----------------------------------------------------
PSQL_ARGS=()
conn_psql_args() { # <conn> -> validated psql/pg_dump host/port/user args
  PSQL_ARGS=(
    -h "$(pg18_conn_field "$1" Host)"
    -p "$(pg18_conn_field "$1" Port)"
    -U "$(pg18_conn_field "$1" Username)"
  )
}

with_database() { # <conn> <new-database-name>
  local conn host port username password new_db="${2:-}"
  conn="$(pg18_canonical_connection "${1:-}")" || return 1
  pg18_is_disposable_db_name "$new_db" || {
    printf 'PG18 parity lane refused: derived database name is not a safe disposable identifier.\n' >&2
    return 1
  }
  host="$(pg18_conn_field "$conn" Host)"
  port="$(pg18_conn_field "$conn" Port)"
  username="$(pg18_conn_field "$conn" Username)"
  password="$(pg18_conn_field "$conn" Password)"
  printf 'Host=%s;Port=%s;Database=%s;Username=%s;Password=%s\n' \
    "$host" "$port" "$new_db" "$username" "$password"
}

server_major() { # <conn>
  local out
  conn_psql_args "$1"
  out="$(PGPASSWORD="$(pg18_conn_field "$1" Password)" psql "${PSQL_ARGS[@]}" \
    -d "$(pg18_conn_field "$1" Database)" -tAc 'SHOW server_version;' 2>/dev/null)" || return 1
  printf '%s\n' "${out%%.*}"
}

ensure_db() { # <conn> creates the disposable database when absent
  local db quoted_db quoted_literal
  local -a args=()
  db="$(pg18_conn_field "$1" Database)"
  quoted_db="$(pg18_sql_identifier "$db")" || return 1
  quoted_literal="$(pg18_sql_literal "$db")" || return 1
  conn_psql_args "$1"
  args=("${PSQL_ARGS[@]}")
  PGPASSWORD="$(pg18_conn_field "$1" Password)" psql "${args[@]}" -d postgres -tAc \
    "SELECT 1 FROM pg_database WHERE datname=$quoted_literal" | grep -q 1 \
    || PGPASSWORD="$(pg18_conn_field "$1" Password)" psql "${args[@]}" -d postgres \
         -c "CREATE DATABASE $quoted_db" >/dev/null
}

# Recreate the test database from empty so EnsureCreated() builds the current
# model. Allowed only after the disposable prefix guard passed, which is what
# makes the DROP safe.
reset_db() { # <conn>
  local db quoted_db
  local -a args=()
  pg18_assert_disposable_connection "$1" || exit 1
  db="$(pg18_conn_field "$1" Database)"
  quoted_db="$(pg18_sql_identifier "$db")" || exit 1
  conn_psql_args "$1"
  args=("${PSQL_ARGS[@]}")
  PGPASSWORD="$(pg18_conn_field "$1" Password)" psql "${args[@]}" -d postgres -c \
    "DROP DATABASE IF EXISTS $quoted_db WITH (FORCE)" >/dev/null
  ensure_db "$1"
}

assert_major() { # <conn> <label> <expected-major>
  local got
  got="$(server_major "$1")" || fail "$2 is unreachable"
  [[ "$got" == "$3" ]] || fail "$2 reports PostgreSQL $got, not $3 - refusing to report $3 qualification from a $2 run"
  printf '  %s server major = %s\n' "$2" "$got"
}

dump_columns() { # <conn> <out-file> stable per-column type inventory
  local -a args=()
  conn_psql_args "$1"
  args=("${PSQL_ARGS[@]}")
  PGPASSWORD="$(pg18_conn_field "$1" Password)" psql "${args[@]}" \
    -d "$(pg18_conn_field "$1" Database)" -tAc \
    "select table_name, column_name, data_type, coalesce(character_maximum_length::text,'-'), coalesce(numeric_precision::text,'-'), coalesce(numeric_scale::text,'-'), is_nullable from information_schema.columns where table_schema='public' order by 1,2" > "$2"
}

# --- resolve and guard the target -------------------------------------------
base=""
for name in "${urls[@]}"; do
  if [[ -n "${!name:-}" ]]; then base="${!name}"; break; fi
done
[[ -n "$base" ]] || fail "no target connection string supplied for mode '$mode'"
base="$(pg18_canonical_connection "$base")" || exit 1
pg18_assert_disposable_connection "$base" || exit 1

base_db="$(pg18_conn_field "$base" Database)"
test_conn="$base"
if [[ -n "${PG18_LADDER_URL:-}" ]]; then
  ladder_conn="$(pg18_canonical_connection "$PG18_LADDER_URL")" || exit 1
else
  ladder_conn="$(with_database "$base" "${base_db}_ladder")" || exit 1
fi
test_db="$(pg18_conn_field "$test_conn" Database)"
ladder_db="$(pg18_conn_field "$ladder_conn" Database)"
if [[ "$ladder_db" == "$test_db" ]]; then
  fail "the migration ladder and the test run must use different databases"
fi
pg18_assert_disposable_connection "$ladder_conn" || exit 1

printf '%s\n' "--- tested revision: $(git rev-parse HEAD)"
printf '%s\n' "--- event: ${GITHUB_EVENT_NAME:-local} / workflow: ${GITHUB_WORKFLOW:-local}"
printf '%s\n' "--- expected major: $expected_major / mode: $mode"
printf '%s\n' "--- test database:   $(pg18_conn_field "$test_conn" Database)"
printf '%s\n' "--- ladder database: $(pg18_conn_field "$ladder_conn" Database)"

if [[ "$mode" == "schema-diff" ]]; then
  pg15_conn="$(pg18_canonical_connection "${PG15_URL:-}")" || exit 1
  pg18_conn="$(pg18_canonical_connection "${PG18_URL:-}")" || exit 1
  step "PG15/PG18 database identity preflight"
  pg18_assert_distinct_database_targets "$pg15_conn" "$pg18_conn" || exit 1
  printf '  PG15 and PG18 targets resolve to distinct canonical database identities\n'
  step "PostgreSQL 18 identity"
  ensure_db "$pg18_conn"
  assert_major "$pg18_conn" "PG18 target" 18
  step "PostgreSQL 15 control identity"
  ensure_db "$pg15_conn"
  assert_major "$pg15_conn" "PG15 control" 15
  step "Migration ladder on PG15 control"
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$pg15_conn"
  step "Migration ladder on PG18"
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$pg18_conn"
  step "Schema parity diff"
  dump_columns "$pg15_conn" /tmp/cols15.txt
  dump_columns "$pg18_conn" /tmp/cols18.txt
  if diff -u /tmp/cols15.txt /tmp/cols18.txt > /tmp/pg18-schema.diff; then
    printf '  column-level schema identical across PG15 and PG18 (%s columns)\n' "$(wc -l < /tmp/cols18.txt)"
  else
    printf '  schema differs; first differences:\n'
    head -40 /tmp/pg18-schema.diff
    cp /tmp/pg18-schema.diff "${RUNNER_TEMP:-/tmp}/pg18-schema.diff" 2>/dev/null || true
    fail "PG15/PG18 schema divergence detected"
  fi
else
  step "PostgreSQL $expected_major identity"
  ensure_db "$ladder_conn"
  assert_major "$ladder_conn" "target" "$expected_major"

  step "dotnet-ef availability"
  if ! dotnet ef --version >/dev/null 2>&1; then
    fail "the dotnet-ef tool is unavailable; run 'dotnet tool restore' from the repository root first"
  fi

  step "EF model consistency"
  dotnet ef migrations has-pending-model-changes --project apps/api --startup-project apps/api

  step "Migration ladder on the disposable database"
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$ladder_conn"

  step "Fresh disposable test database (EnsureCreated path, as the api job uses)"
  reset_db "$test_conn"

  # The reported regression results come from this database, not the migration
  # ladder. Attest its live server major after reset and immediately before the
  # suite so a split PG18-ladder / PG15-test configuration cannot pass.
  assert_major "$test_conn" "test database" "$expected_major"

  step "$mode regression (fake Manus/Bridge, no providers, no charging)"
  printf '  expected server major: %s\n' "$expected_major"
  export ConnectionStrings__Postgres="$test_conn" Database__ApplyMigrations=false
  dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --filter "$filter" 2>&1 \
    | tee "$artifact" | tail -3
  # The dotnet test summary pads its counters with spaces
  # ("Failed:     0, Passed:   224"), so the counters are parsed rather than
  # string-matched; a naive `grep -q "Failed: 0"` never matches and reports a
  # false RED, which the first re-run exposed.
  summary="$(grep -E 'Failed: *[0-9]+, Passed: *[0-9]+' "$artifact" | tail -1)"
  [[ -n "$summary" ]] || fail "no test summary found in $artifact"
  failed_count="$(printf '%s\n' "$summary" | sed -E 's/.*Failed: *([0-9]+),.*/\1/')"
  passed_count="$(printf '%s\n' "$summary" | sed -E 's/.*, Passed: *([0-9]+).*/\1/')"
  printf '  summary: failed=%s passed=%s\n' "$failed_count" "$passed_count"
  [[ "$failed_count" == "0" ]] || fail "$mode reported $failed_count failures"
  [[ "$passed_count" -gt 0 ]] || fail "$mode executed no tests"
fi

step "Production-image assumptions"
python3 - <<'PY'
# Production runs ghcr.io/railwayapp-templates/postgres-ssl:18, i.e. stock
# PostgreSQL 18 with TLS handled by the image, not an extension-rich custom
# build. Record the migrations' extension requirements instead of silently
# trusting that postgres:18 is a faithful parity target.
import pathlib, re
# A migration class is "<timestamp>_<Name>.cs"; ModelSnapshot and Designer
# files are excluded so the reported count is the real ladder length.
migrations = [p for p in pathlib.Path("apps/api/Persistence/Migrations").glob("*.cs")
              if re.match(r"^\d{8,}_", p.name) and not p.name.endswith(".Designer.cs")]
lower = "\n".join(p.read_text() for p in migrations).lower()
required = [e for e in ("postgis", "pg_trgm", "uuid-ossp", "hstore", "citext", "btree_gist")
            if f'create extension "{e}"' in lower]
print(f"  {len(migrations)} migration classes scanned; extension dependencies: {required or 'none (core PostgreSQL only)'}")
if required:
    raise SystemExit(f"PG18 parity lane must provision these extensions: {required}")
PY

printf 'PG18 parity: PASS (%s, PostgreSQL %s) - disposable databases only; no production database, provider call, deploy, or charge performed.\n' "$mode" "$expected_major"
