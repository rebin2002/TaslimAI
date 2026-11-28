#!/usr/bin/env bash
# Offline positive controls for the PG18/PG15 parity harness.
# psql and dotnet are PATH stubs: no socket, database, migration, test runner,
# provider, or network is used. The fake server versions are per-database.
set -Eeuo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HARNESS="$ROOT_DIR/scripts/pg18-parity-gate.sh"
SANDBOX="$(mktemp -d)"
trap 'rm -rf "$SANDBOX"' EXIT
BIN="$SANDBOX/bin"
mkdir -p "$BIN"

cat >"$BIN/psql" <<'STUB'
#!/usr/bin/env bash
DB=""
QUERY=""
HOST=""
PORT=""
printf 'psql %s\n' "$*" >> "${PG18_FAKE_CALLLOG:-/dev/null}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    -h) HOST="${2:-}"; shift 2 ;;
    -p) PORT="${2:-}"; shift 2 ;;
    -d) DB="${2:-}"; shift 2 ;;
    -tAc|-c) QUERY="${2:-}"; shift 2 ;;
    *) shift ;;
  esac
done
if [[ "$QUERY" == *"SHOW server_version"* ]]; then
  IFS=';' read -ra PAIRS <<< "${PG18_FAKE_VERSIONS:-}"
  for pair in "${PAIRS[@]}"; do
    [[ -z "$pair" ]] && continue
    key="${pair%%=*}"
    if [[ "$key" == "$DB" || "$key" == "$HOST:$PORT:$DB" ]]; then
      printf '%s\n' "${pair#*=}"
      exit 0
    fi
  done
  printf 'fake psql has no version for database %s\n' "$DB" >&2
  exit 2
fi
if [[ "$QUERY" == *"FROM pg_database"* ]]; then
  printf '1\n'
fi
exit 0
STUB

cat >"$BIN/dotnet" <<'STUB'
#!/usr/bin/env bash
printf 'dotnet %s\n' "$*" >> "${PG18_FAKE_CALLLOG:-/dev/null}"
case "${1:-} ${2:-}" in
  "ef --version") printf '8.0.11\n'; exit 0 ;;
  "ef "*|"ef") exit 0 ;;
  "test "*|"test")
    printf 'test-connection %s\n' "${ConnectionStrings__Postgres:-}" >> "${PG18_FAKE_CALLLOG:-/dev/null}"
    printf 'Test run for Taslim.Api.Tests\n'
    printf 'Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1\n'
    exit 0 ;;
esac
exit 0
STUB
chmod +x "$BIN/psql" "$BIN/dotnet"

pass=0
failures=0
assert_matched() { # <mode> <expected-major> <version> <slug>
  local mode="$1" expected="$2" version="$3" slug="$4"
  local test_db="taslim_pg18_matched_${slug}_test"
  local ladder_db="taslim_pg18_matched_${slug}_ladder"
  local conn="Password=stub-only;Database=$test_db;Username=taslim;Port=05432;Host=127.0.0.1"
  local ladder="Username=taslim;Host=127.0.0.1;Database=$ladder_db;Password=stub-only;Port=05432"
  local versions="$test_db=$version;$ladder_db=$version" out
  if out="$(env \
      PATH="$BIN:$PATH" \
      PG18_PARITY_LOCAL_OPT_IN=1 \
      GITHUB_EVENT_NAME= GITHUB_REF= GITHUB_BASE_REF= GITHUB_HEAD_REF= \
      PG18_FAKE_VERSIONS="$versions" \
      PG18_FAKE_CALLLOG="$SANDBOX/calls.log" \
      PG18_TEST_URL="$conn" PG15_TEST_URL="$conn" \
      PG18_LADDER_URL="$ladder" \
      ConnectionStrings__Postgres= FRESH_DATABASE_URL= \
      bash "$HARNESS" "$mode" 2>&1)"; then
    if grep -Fq "target server major = $expected" <<<"$out" \
      && grep -Fq "test database server major = $expected" <<<"$out" \
      && grep -Fq "PG18 parity: PASS ($mode, PostgreSQL $expected)" <<<"$out"; then
      printf '  ok   %s matched ladder/test major %s passes\n' "$mode" "$expected"
      pass=$((pass + 1))
    else
      printf '  FAIL %s did not attest both major %s servers\n' "$mode" "$expected"
      printf '%s\n' "$out" | grep -E 'server major|PG18 parity:|reports PostgreSQL' || true
      failures=$((failures + 1))
    fi
  else
    printf '  FAIL %s matched-major run refused\n' "$mode"
    printf '%s\n' "$out" | tail -20
    failures=$((failures + 1))
  fi
}

printf '== matched-major offline positive controls (stubbed tools only) ==\n'
assert_matched autopilot 18 18.7 pg18_autopilot
assert_matched billing 18 18.7 pg18_billing
assert_matched autopilot-control 15 15.20 pg15_autopilot
assert_matched billing-control 15 15.20 pg15_billing
if grep -Fq -- 'dotnet ef database update --project apps/api --startup-project apps/api --connection Host=127.0.0.1;Port=5432;Database=taslim_pg18_matched_pg18_autopilot_ladder;Username=taslim;Password=stub-only' "$SANDBOX/calls.log" \
  && grep -Fq -- 'test-connection Host=127.0.0.1;Port=5432;Database=taslim_pg18_matched_pg18_autopilot_test;Username=taslim;Password=stub-only' "$SANDBOX/calls.log" \
  && grep -Fq -- 'psql -h 127.0.0.1 -p 5432 -U taslim' "$SANDBOX/calls.log"; then
  printf '  ok   psql and EF/test use the same canonicalized connection fields\n'
  pass=$((pass + 1))
else
  printf '  FAIL canonical connection was not consistent across psql and EF/test\n'
  grep -E 'dotnet ef database update|test-connection|psql -h' "$SANDBOX/calls.log" || true
  failures=$((failures + 1))
fi

run_schema_diff() { # <call-log> <versions> <PG15-url> <PG18-url> -> output
  local call_log="$1" versions="$2" pg15_url="$3" pg18_url="$4"
  : > "$call_log"
  env \
    PATH="$BIN:$PATH" \
    PG18_PARITY_LOCAL_OPT_IN=1 \
    GITHUB_EVENT_NAME= GITHUB_REF= GITHUB_BASE_REF= GITHUB_HEAD_REF= \
    PG18_FAKE_VERSIONS="$versions" \
    PG18_FAKE_CALLLOG="$call_log" \
    PG15_URL="$pg15_url" PG18_URL="$pg18_url" \
    FRESH_DATABASE_URL= ConnectionStrings__Postgres= \
    bash "$HARNESS" schema-diff 2>&1
}

printf '\n== schema-diff identity preflight (offline stubs only) ==\n'
same_db="taslim_pg18_schema_shared"
alias_call_log="$SANDBOX/schema-alias-refused.log"
alias_out=""
if alias_out="$(run_schema_diff "$alias_call_log" "" \
    "Host=localhost;Port=05432;Database=$same_db;Username=pg15user;Password=pg15-secret" \
    "Host=127.0.0.1;Port=5432;Database=$same_db;Username=pg18user;Password=pg18-secret")"; then
  printf '  FAIL schema-diff accepted the same DB through host aliases and changed credentials\n'
  failures=$((failures + 1))
elif [[ ! -s "$alias_call_log" ]] && grep -qi 'same PostgreSQL database' <<<"$alias_out"; then
  printf '  ok   aliased same-DB target refused before any psql or EF call, credentials ignored\n'
  pass=$((pass + 1))
else
  printf '  FAIL alias collision was not refused by the preflight before database commands\n'
  printf '%s\n' "$alias_out" | tail -12
  cat "$alias_call_log"
  failures=$((failures + 1))
fi

ambiguous_call_log="$SANDBOX/schema-ambiguous-refused.log"
ambiguous_out=""
if ambiguous_out="$(run_schema_diff "$ambiguous_call_log" "" \
    "Host=postgres;Port=5432;Database=$same_db;Username=taslim;Password=offline-only" \
    "Host=db;Port=5432;Database=$same_db;Username=taslim;Password=offline-only")"; then
  printf '  FAIL schema-diff accepted unverified host aliases with the same database name\n'
  failures=$((failures + 1))
elif [[ ! -s "$ambiguous_call_log" ]] && grep -qi 'ambiguous' <<<"$ambiguous_out"; then
  printf '  ok   ambiguous host aliases refused before any psql or EF call\n'
  pass=$((pass + 1))
else
  printf '  FAIL ambiguous target was not rejected before database commands\n'
  printf '%s\n' "$ambiguous_out" | tail -12
  cat "$ambiguous_call_log"
  failures=$((failures + 1))
fi

# Different service ports identify separate instances even with the same
# database name. This mirrors the workflow's localhost:5432/localhost:5433
# PG15/PG18 service mapping and proves database-name-only rejection is wrong.
positive_call_log="$SANDBOX/schema-distinct-shared-name.log"
positive_out=""
if positive_out="$(run_schema_diff "$positive_call_log" \
    "localhost:5432:$same_db=15.20;localhost:5433:$same_db=18.7" \
    "Host=localhost;Port=5432;Database=$same_db;Username=pg15user;Password=pg15-secret" \
    "Host=localhost;Port=5433;Database=$same_db;Username=pg18user;Password=pg18-secret")"; then
  if grep -Fq 'PG15 control server major = 15' <<<"$positive_out" \
    && grep -Fq 'PG18 target server major = 18' <<<"$positive_out" \
    && grep -Fq 'column-level schema identical across PG15 and PG18' <<<"$positive_out" \
    && grep -Fq -- 'psql -h localhost -p 5432' "$positive_call_log" \
    && grep -Fq -- 'psql -h localhost -p 5433' "$positive_call_log" \
    && grep -Fq -- "Database=$same_db;Username=pg15user;Password=pg15-secret" "$positive_call_log" \
    && grep -Fq -- "Database=$same_db;Username=pg18user;Password=pg18-secret" "$positive_call_log"; then
    printf '  ok   distinct PG15/PG18 endpoints may share a DB name; offline schema-diff control completes\n'
    pass=$((pass + 1))
  else
    printf '  FAIL distinct same-name schema-diff control missed a major/target assertion\n'
    printf '%s\n' "$positive_out" | tail -20
    cat "$positive_call_log"
    failures=$((failures + 1))
  fi
else
  printf '  FAIL distinct same-name schema-diff control was refused\n'
  printf '%s\n' "$positive_out" | tail -20
  failures=$((failures + 1))
fi

printf '\npg18-parity-gate.matched-controls.test.sh: %s passed, %s failed\n' "$pass" "$failures"
[[ "$failures" -eq 0 ]]
