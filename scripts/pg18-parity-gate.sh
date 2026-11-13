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
#   * The expected major is asserted from the live server, so a PG15 run can
#     never be reported as PG18 qualification and a PG18 run can never
#     silently replace the PG15 control.
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
conn_psql_args() { # <conn> -> psql/pg_dump host/port/user args
  printf -- '-h %s -p %s -U %s' \
    "$(pg18_conn_field "$1" Host)" "$(pg18_conn_field "$1" Port)" "$(pg18_conn_field "$1" Username)"
}

with_database() { # <conn> <new-database-name>
  printf '%s\n' "${1//Database=$(pg18_conn_field "$1" Database)/Database=$2}"
}

server_major() { # <conn>
  local out
  out="$(PGPASSWORD="$(pg18_conn_field "$1" Password)" psql $(conn_psql_args "$1") \
    -d "$(pg18_conn_field "$1" Database)" -tAc 'SHOW server_version;' 2>/dev/null)" || return 1
  printf '%s\n' "${out%%.*}"
}

ensure_db() { # <conn> creates the disposable database when absent
  local db args
  db="$(pg18_conn_field "$1" Database)"
  args="$(conn_psql_args "$1")"
  PGPASSWORD="$(pg18_conn_field "$1" Password)" psql $args -d postgres -tAc \
    "SELECT 1 FROM pg_database WHERE datname='$db'" | grep -q 1 \
    || PGPASSWORD="$(pg18_conn_field "$1" Password)" psql $args -d postgres \
         -c "CREATE DATABASE \"$db\"" >/dev/null
}

# Recreate the test database from empty so EnsureCreated() builds the current
# model. Allowed only after the disposable prefix guard passed, which is what
# makes the DROP safe.
reset_db() { # <conn>
  local db args
  pg18_assert_disposable_connection "$1" || exit 1
  db="$(pg18_conn_field "$1" Database)"
  args="$(conn_psql_args "$1")"
  PGPASSWORD="$(pg18_conn_field "$1" Password)" psql $args -d postgres -c \
    "DROP DATABASE IF EXISTS \"$db\" WITH (FORCE)" >/dev/null
  ensure_db "$1"
}

assert_major() { # <conn> <label> <expected-major>
  local got
  got="$(server_major "$1")" || fail "$2 is unreachable"
  [[ "$got" == "$3" ]] || fail "$2 reports PostgreSQL $got, not $3 - refusing to report $3 qualification from a $2 run"
  printf '  %s server major = %s\n' "$2" "$got"
}

dump_columns() { # <conn> <out-file> stable per-column type inventory
  PGPASSWORD="$(pg18_conn_field "$1" Password)" psql $(conn_psql_args "$1") \
    -d "$(pg18_conn_field "$1" Database)" -tAc \
    "select table_name, column_name, data_type, coalesce(character_maximum_length::text,'-'), coalesce(numeric_precision::text,'-'), coalesce(numeric_scale::text,'-'), is_nullable from information_schema.columns where table_schema='public' order by 1,2" > "$2"
}

# --- resolve and guard the target -------------------------------------------
base=""
for name in "${urls[@]}"; do
  if [[ -n "${!name:-}" ]]; then base="${!name}"; break; fi
done
[[ -n "$base" ]] || fail "no target connection string supplied for mode '$mode'"
pg18_assert_disposable_connection "$base" || exit 1

base_db="$(pg18_conn_field "$base" Database)"
test_conn="$base"
ladder_conn="${PG18_LADDER_URL:-$(with_database "$base" "${base_db}_ladder")}"
if [[ "$ladder_conn" == "$test_conn" ]]; then
  fail "the migration ladder and the test run must use different databases"
fi
pg18_assert_disposable_connection "$ladder_conn" || exit 1

printf '%s\n' "--- tested revision: $(git rev-parse HEAD)"
printf '%s\n' "--- event: ${GITHUB_EVENT_NAME:-local} / workflow: ${GITHUB_WORKFLOW:-local}"
printf '%s\n' "--- expected major: $expected_major / mode: $mode"
printf '%s\n' "--- test database:   $(pg18_conn_field "$test_conn" Database)"
printf '%s\n' "--- ladder database: $(pg18_conn_field "$ladder_conn" Database)"

if [[ "$mode" == "schema-diff" ]]; then
  step "PostgreSQL 18 identity"
  ensure_db "${PG18_URL:-}"
  assert_major "${PG18_URL:-}" "PG18 target" 18
  step "PostgreSQL 15 control identity"
  pg18_assert_disposable_connection "${PG15_URL:-}" || exit 1
  ensure_db "${PG15_URL:-}"
  assert_major "${PG15_URL:-}" "PG15 control" 15
  step "Migration ladder on PG15 control"
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$PG15_URL"
  step "Migration ladder on PG18"
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$PG18_URL"
  step "Schema parity diff"
  dump_columns "$PG15_URL" /tmp/cols15.txt
  dump_columns "$PG18_URL" /tmp/cols18.txt
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

  step "EF model consistency"
  dotnet ef migrations has-pending-model-changes --project apps/api --startup-project apps/api

  step "Migration ladder on the disposable database"
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$ladder_conn"

  step "Fresh disposable test database (EnsureCreated path, as the api job uses)"
  reset_db "$test_conn"

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
