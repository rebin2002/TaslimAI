#!/usr/bin/env bash
# Independent release-evidence audit of scripts/pg18-parity-gate.sh for issue #734.
#
# WHAT THIS CHECKS
# The parity lane's central safety claim is that a PostgreSQL 15 execution can
# never be reported as PostgreSQL 18 qualification. In the `autopilot` and
# `billing` modes the harness opens TWO separate databases:
#
#   * the ladder database  - receives `dotnet ef database update`
#   * the test  database   - receives `dotnet test`, i.e. the 224/224 Autopilot
#                            and 72/72 Billing counts that ARE the evidence
#
# `assert_major` is called on the LADDER database only (pg18-parity-gate.sh
# line 194); the test database is created by `reset_db` (line 208) and then used
# for the regression run without its server ever being identified. The two are
# only compared as literal strings (lines 157-159), so they may point at
# different servers. In hosted CI both come from one service container, so PG18
# holds by construction; on the documented local opt-in path
# (PG18_PARITY_LOCAL_OPT_IN=1) a human types both connection strings, and the
# same env vars are also overridable in a hosted job, so a PG18 ladder paired
# with a PG15 test database currently still prints
# "PG18 parity: PASS (autopilot, PostgreSQL 18)".
#
# That is the narrow, provisioning/cleanup attribution gap this script pins. It
# is the sibling of the existing collision guard, which proves the authors
# already accept that a mis-split target is a real hazard: `schema-diff`
# asserts BOTH of its servers (lines 171 and 175), the split-target modes assert
# only one.
#
# HOW IT STAYS SAFE
# Purely offline and deterministic. `psql` and `dotnet` are PATH stubs in a
# temporary directory that open no socket, run no migration and touch no
# database; the stubbed `psql` answers SHOW server_version from a name table.
# Nothing here can reach a real or production server, and this script is
# deliberately wired into no workflow trigger.
#
# EXIT CODES
#   0  every contract holds (the gap has been fixed in the harness)
#   1  one or more SAFETY contracts fail - a regression, always actionable
#   2  only the documented PARITY-ATTRIBUTION gap is still open
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HARNESS="$ROOT_DIR/scripts/pg18-parity-gate.sh"
[[ -f "$HARNESS" ]] || { printf 'harness %s not found\n' "$HARNESS" >&2; exit 1; }

STRICT=0
for arg in "$@"; do
  case "$arg" in
    --strict) STRICT=1 ;;
    *) printf 'unknown argument: %s\n' "$arg" >&2; exit 64 ;;
  esac
done

pass=0
safety_fail=0
attribution_fail=0

ok()      { printf '  ok   %s\n' "$1"; pass=$((pass + 1)); }
safety()  { printf '  FAIL %s\n' "$1"; safety_fail=$((safety_fail + 1)); }
attrib()  { printf '  GAP  %s\n' "$1"; attribution_fail=$((attribution_fail + 1)); }

# --- fake toolchain ---------------------------------------------------------
SANDBOX="$(mktemp -d)"
trap 'rm -rf "$SANDBOX"' EXIT
BIN="$SANDBOX/bin"
mkdir -p "$BIN"

# server_version answered from a database-name -> version table, so the same
# stub can present a PG18 ladder next to a PG15 test database.
cat >"$BIN/psql" <<'STUB'
#!/usr/bin/env bash
# Offline stand-in for psql. Never opens a connection.
DB=""
QUERY=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    -d) DB="${2:-}"; shift 2 ;;
    -tAc|-c) QUERY="${2:-}"; shift 2 ;;
    *) shift ;;
  esac
done
TABLE="${PG18_FAKE_VERSIONS:-}"
version=""
IFS=';' read -ra PAIRS <<< "$TABLE"
for pair in "${PAIRS[@]}"; do
  [[ -z "$pair" ]] && continue
  if [[ "${pair%%=*}" == "$DB" ]]; then version="${pair#*=}"; break; fi
done
case "$QUERY" in
  *"SHOW server_version"*)
    if [[ -n "$version" ]]; then printf '%s\n' "$version"; exit 0; fi
    printf 'psql-fake: no version for database %s\n' "$DB" >&2
    exit 2
    ;;
  *"FROM pg_database"*)
    printf '1\n'; exit 0 ;;
esac
exit 0
STUB

cat >"$BIN/dotnet" <<'STUB'
#!/usr/bin/env bash
# Offline stand-in for dotnet. Performs no build, no migration, no test run.
LOG="${PG18_FAKE_CALLLOG:-/dev/null}"
printf '%s\n' "$*" >> "$LOG"
case "${1:-} ${2:-}" in
  "ef --version") printf '8.0.11\n'; exit 0 ;;
  "ef "*|"ef")   exit 0 ;;
  "test "*|"test")
    printf 'Test run for Taslim.Api.Tests\n'
    printf 'Passed!  - Failed:     0, Passed:   224, Skipped:     0, Total:   224\n'
    exit 0 ;;
esac
exit 0
STUB

chmod +x "$BIN/psql" "$BIN/dotnet"
export PATH="$BIN:$PATH"

# Run the harness with a ladder/test database split. Both names keep the
# disposable taslim_pg18_ prefix so the naming guards stay satisfied and this
# exercises attribution only, never the production-target refuses.
run_harness() { # <mode> <versions> <ladder-db> <test-db> -> stdout+stderr
  local mode="$1" versions="$2" ladder_db="$3" test_db="$4"
  env \
    PATH="$BIN:$PATH" \
    PG18_PARITY_LOCAL_OPT_IN=1 \
    GITHUB_EVENT_NAME= GITHUB_REF= GITHUB_BASE_REF= GITHUB_HEAD_REF= \
    PG18_FAKE_VERSIONS="$versions" \
    PG18_FAKE_CALLLOG="$SANDBOX/calls.log" \
    PG18_TEST_URL="Host=127.0.0.1;Port=55432;Database=$test_db;Username=taslim;Password=change-me" \
    PG18_LADDER_URL="Host=127.0.0.1;Port=55432;Database=$ladder_db;Username=taslim;Password=change-me" \
    ConnectionStrings__Postgres= FRESH_DATABASE_URL= \
    bash "$HARNESS" "$mode" 2>&1
}

V_18_18="taslim_pg18_aud_ladder=18.7;taslim_pg18_aud_test=18.7"
V_18_15="taslim_pg18_aud_ladder=18.7;taslim_pg18_aud_test=15.20"
V_15_18="taslim_pg18_aud_ladder=15.20;taslim_pg18_aud_test=18.7"

printf '== PG18 parity: test-database major attribution (issue #734) ==\n'

# --- safety contracts -------------------------------------------------------
# These must hold no matter how attribution is resolved.

out="$(run_harness billing "$V_18_18" taslim_pg18_aud_ladder taslim_pg18_aud_test)" && rc=0 || rc=$?
if [[ $rc -eq 0 ]] && grep -q "PG18 parity: PASS" <<<"$out"; then
  ok  "matched PG18 ladder + PG18 test database still passes"
else
  safety "matched PG18 pair regressed to a refusal (rc=$rc)"
  printf '%s\n' "$out" | sed 's/^/       /'
fi

# A production-shaped host must still be refused before any connection, which
# is the guard this script must never weaken.
out="$(run_harness billing "x=1" taslim_pg18_aud_ladder taslim_production)" && rc=0 || rc=$?
if [[ $rc -ne 0 ]] && grep -qi "lacks the disposable prefix" <<<"$out"; then
  ok  "non-disposable test database refused by the naming guard"
else
  safety "non-disposable test database was not refused (rc=$rc)"
  printf '%s\n' "$out" | sed 's/^/       /'
fi

out="$(run_harness billing "$V_18_18" taslim_pg18_aud_ladder taslim_pg18_aud_test)" && rc=0 || rc=$?
if [[ $rc -eq 0 ]]; then
  ok  "opt-in context allows the audited local run (guard contract intact)"
else
  safety "opt-in local context unexpectedly refused (rc=$rc)"
  printf '%s\n' "$out" | sed 's/^/       /'
fi

# --- parity-attribution contracts -------------------------------------------
# The behaviour the lane's own documentation promises but does not enforce.

printf '\n== parity attribution: ladder PG18 with a PG15 test database ==\n'
out="$(run_harness autopilot "$V_18_15" taslim_pg18_aud_ladder taslim_pg18_aud_test)" && rc=0 || rc=$?
if [[ $rc -ne 0 ]] && grep -qi "not a disposable\|major\|refus" <<<"$out"; then
  ok  "Autopilot: PG15 test database refused despite a PG18 ladder"
else
  attrib "Autopilot reports PG18 qualification while the 224 tests are bound to a server reporting 15.20"
  grep -E "server major|expected server major|PG18 parity: PASS" <<<"$out" | sed 's/^/       /'
fi

printf '\n== parity attribution: billing evidence on a PG15 test database ==\n'
out="$(run_harness billing "$V_18_15" taslim_pg18_aud_ladder taslim_pg18_aud_test)" && rc=0 || rc=$?
if [[ $rc -ne 0 ]] && grep -qi "major\|refus" <<<"$out"; then
  ok  "Billing: PG15 test database refused despite a PG18 ladder"
else
  attrib "Billing reports PG18 qualification while the ledger tests are bound to a server reporting 15.20"
  grep -E "server major|expected server major|PG18 parity: PASS" <<<"$out" | sed 's/^/       /'
fi

printf '\n== parity attribution: the inverse split (PG15 ladder, PG18 test) ==\n'
out="$(run_harness billing "$V_15_18" taslim_pg18_aud_ladder taslim_pg18_aud_test)" && rc=0 || rc=$?
if [[ $rc -ne 0 ]]; then
  ok  "PG15 ladder with a PG18 test database refused (ladder assertion fires)"
else
  safety "PG15 ladder was accepted as a PG18 target"
  printf '%s\n' "$out" | sed 's/^/       /'
fi

printf '\n== evidence binding: which database actually ran the tests ==\n'
run_harness billing "$V_18_18" taslim_pg18_aud_ladder taslim_pg18_aud_test >/dev/null 2>&1 || true
if [[ -f "$SANDBOX/calls.log" ]] && grep -q "test apps/api.Tests" "$SANDBOX/calls.log"; then
  ok  "the regression evidence comes from dotnet test against the TEST database"
else
  safety "expected a dotnet test invocation in the stubbed call log"
fi
if grep -q "database update" "$SANDBOX/calls.log" 2>/dev/null; then
  ok  "the ladder database is migrated separately from the test database"
else
  safety "expected a dotnet ef database update in the stubbed call log"
fi

printf '\n'
printf 'pg18-parity-gate.major-split.test.sh: %s passed, %s safety failures, %s attribution gaps\n' \
  "$pass" "$safety_fail" "$attribution_fail"

if [[ $safety_fail -gt 0 ]]; then
  printf 'RESULT: SAFETY REGRESSION - the disposable-database guards are no longer intact.\n' >&2
  exit 1
fi
if [[ $attribution_fail -gt 0 ]]; then
  printf 'RESULT: known #734 attribution gap still open. Fix: call assert_major on\n' >&2
  printf '        $test_conn after reset_db, before dotnet test, exactly as schema-diff\n' >&2
  printf '        already does for both of its servers.\n' >&2
  [[ $STRICT -eq 1 ]] && exit 1
  exit 2
fi
printf 'RESULT: all contracts hold - the test-database major is now pinned.\n'
exit 0
