#!/usr/bin/env bash
# Offline guard contracts for scripts/pg18-parity-gate.sh.
# Runs without a database, without dotnet, and without network access.
set -Eeuo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=scripts/lib/pg18-parity-refs.sh
. "$ROOT_DIR/scripts/lib/pg18-parity-refs.sh"

pass=0
fail_count=0

expect_ok() { # <label> <cmd...>
  local label="$1"
  shift
  if "$@" >/dev/null 2>&1; then
    printf '  ok   %s\n' "$label"; pass=$((pass + 1))
  else
    printf '  FAIL %s (expected allow)\n' "$label"; fail_count=$((fail_count + 1))
  fi
}

expect_refused() { # <label> <cmd...>
  local label="$1"
  shift
  if "$@" >/dev/null 2>&1; then
    printf '  FAIL %s (expected refuse)\n' "$label"; fail_count=$((fail_count + 1))
  else
    printf '  ok   %s\n' "$label"; pass=$((pass + 1))
  fi
}

BR="$(pg18_expected_branch)"

echo "== opt-in classification =="
expect_ok    "dispatch on harness branch allowed" \
  pg18_classify_ref workflow_dispatch "refs/heads/$BR" "" "" "$BR"
expect_refused "dispatch from a product branch refused" \
  pg18_classify_ref workflow_dispatch "refs/heads/main" "" "" "main"
expect_ok    "push under ci/postgres18- prefix allowed" \
  pg18_classify_ref push "refs/heads/ci/postgres18-experiment" "" "" ""
expect_refused "push to main refused" \
  pg18_classify_ref push "refs/heads/main" "" "" "main"
expect_refused "push to an unrelated branch refused" \
  pg18_classify_ref push "refs/heads/fix/billing-page-layout" "" "" ""
expect_ok    "PR into the harness branch allowed" \
  pg18_classify_ref pull_request "refs/pull/1/merge" "$BR" "" ""
expect_refused "PR between product branches refused" \
  pg18_classify_ref pull_request "refs/pull/2/merge" "main" "feature/taslim-global-slot-refill-stack" ""
expect_refused "detached HEAD with no PR context and no switch refused" \
  pg18_classify_ref "" "" "" "" ""
expect_refused "unsupported event refused" \
  pg18_classify_ref schedule "refs/heads/$BR" "" "" "$BR"

echo "== explicit local opt-in for composed candidates =="
# The switch is read from the environment when the library is sourced, so each
# direction needs its own subshell.
if bash -c ". '$ROOT_DIR/scripts/lib/pg18-parity-refs.sh'; pg18_classify_ref '' '' '' '' 'tmp/pg18-composed-candidate'" >/dev/null 2>&1; then
  printf '  FAIL local run allowed without the switch\n'; fail_count=$((fail_count + 1))
else
  printf '  ok   local run refused without the switch\n'; pass=$((pass + 1))
fi
if PG18_PARITY_LOCAL_OPT_IN=1 bash -c ". '$ROOT_DIR/scripts/lib/pg18-parity-refs.sh'; pg18_classify_ref '' '' '' '' 'tmp/pg18-composed-candidate'" | grep -q local-opt-in; then
  printf '  ok   local run allowed with PG18_PARITY_LOCAL_OPT_IN=1\n'; pass=$((pass + 1))
else
  printf '  FAIL local run refused despite the switch\n'; fail_count=$((fail_count + 1))
fi

echo "== disposable database guards =="
expect_ok    "disposable underscore name accepted" \
  pg18_is_disposable_db_name "taslim_pg18_billing"
expect_ok    "disposable hyphen name accepted" \
  pg18_is_disposable_db_name "taslim-pg18-autopilot"
expect_refused "production database name refused" \
  pg18_is_disposable_db_name "taslim_production"
expect_refused "plain production database name refused" \
  pg18_is_disposable_db_name "taslim"
expect_refused "empty database name refused" \
  pg18_is_disposable_db_name ""

expect_ok    "loopback disposable connection accepted" \
  pg18_assert_disposable_connection "Host=localhost;Port=5432;Database=taslim_pg18_billing;Username=taslim;Password=change-me"
expect_ok    "service-host disposable connection accepted" \
  pg18_assert_disposable_connection "Host=postgres;Port=5432;Database=taslim_pg18_autopilot;Username=taslim;Password=change-me"
expect_refused "railway production URL refused" \
  pg18_assert_disposable_connection "Host=containers.vpc-railway.internal;Port=5432;Database=taslim;Username=postgres;Password=real"
expect_refused "non-disposable database on loopback refused" \
  pg18_assert_disposable_connection "Host=localhost;Port=5432;Database=taslim;Username=taslim;Password=change-me"
expect_refused "empty connection refused" \
  pg18_assert_disposable_connection ""

echo "== connection parsing =="
got="$(pg18_conn_field "Host=localhost;Port=55432;Database=taslim_pg18_billing;Username=taslim" Database)"
if [[ "$got" == "taslim_pg18_billing" ]]; then
  printf '  ok   Database field parsed\n'; pass=$((pass + 1))
else
  printf '  FAIL Database field parsed as %s\n' "$got"; fail_count=$((fail_count + 1))
fi
got="$(pg18_conn_field "Host=localhost;Port=55432;Database=taslim_pg18_billing" Host)"
if [[ "$got" == "localhost" ]]; then
  printf '  ok   Host field parsed\n'; pass=$((pass + 1))
else
  printf '  FAIL Host field parsed as %s\n' "$got"; fail_count=$((fail_count + 1))
fi

echo "== fail-closed behaviour of the harness itself =="
if GITHUB_EVENT_NAME=push GITHUB_REF="refs/heads/main" \
   bash "$ROOT_DIR/scripts/pg18-parity-gate.sh" autopilot >/dev/null 2>&1; then
  printf '  FAIL harness ran in a refused context\n'; fail_count=$((fail_count + 1))
else
  printf '  ok   harness refuses outside opt-in contexts\n'; pass=$((pass + 1))
fi

# A refused context must be reported even when a production-looking URL is set,
# proving the guard order (context first, connection second).
if GITHUB_EVENT_NAME=push GITHUB_REF="refs/heads/main" \
   FRESH_DATABASE_URL="Host=containers.vpc-railway.internal;Database=taslim;Username=postgres;Password=real" \
   bash "$ROOT_DIR/scripts/pg18-parity-gate.sh" autopilot 2>/tmp/pg18-refuse.log; then
  printf '  FAIL harness accepted a production URL\n'; fail_count=$((fail_count + 1))
else
  if grep -qi "not a disposable" /tmp/pg18-refuse.log; then
    printf '  FAIL refused for the wrong reason (connection checked after context)\n'; fail_count=$((fail_count + 1))
  else
    printf '  ok   refusal reason is the opt-in context\n'; pass=$((pass + 1))
  fi
fi

echo "== mode and database-collision fail-closed cases =="
if PG18_PARITY_LOCAL_OPT_IN=1 GITHUB_EVENT_NAME= GITHUB_REF= ConnectionStrings__Postgres="Host=127.0.0.1;Port=55432;Database=taslim_pg18_guard;Username=taslim;Password=change-me" \
   bash "$ROOT_DIR/scripts/pg18-parity-gate.sh" bogus >/dev/null 2>&1; then
  printf '  FAIL unknown mode accepted\n'; fail_count=$((fail_count + 1))
else
  printf '  ok   unknown mode rejected before any database work\n'; pass=$((pass + 1))
fi

# The ladder and the test run must never share a database: the first local
# run proved that this produces false PG18 failures.
if PG18_PARITY_LOCAL_OPT_IN=1 GITHUB_EVENT_NAME= GITHUB_REF= \
   PG18_TEST_URL="Host=127.0.0.1;Port=55432;Database=taslim_pg18_guard;Username=taslim;Password=change-me" \
   PG18_LADDER_URL="Host=127.0.0.1;Port=55432;Database=taslim_pg18_guard;Username=taslim;Password=change-me" \
   bash "$ROOT_DIR/scripts/pg18-parity-gate.sh" billing 2>/tmp/pg18-collide.log; then
  printf '  FAIL ladder/test database collision accepted\n'; fail_count=$((fail_count + 1))
else
  if grep -qi "different databases" /tmp/pg18-collide.log; then
    printf '  ok   ladder/test database collision refused\n'; pass=$((pass + 1))
  else
    printf '  FAIL collision refused for the wrong reason\n'; fail_count=$((fail_count + 1))
  fi
fi

# Missing connection string must refuse rather than fall back to a default.
if PG18_PARITY_LOCAL_OPT_IN=1 GITHUB_EVENT_NAME= GITHUB_REF= ConnectionStrings__Postgres= FRESH_DATABASE_URL= PG18_TEST_URL= \
   bash "$ROOT_DIR/scripts/pg18-parity-gate.sh" billing >/dev/null 2>&1; then
  printf '  FAIL empty target accepted\n'; fail_count=$((fail_count + 1))
else
  printf '  ok   empty target refused (no silent default)\n'; pass=$((pass + 1))
fi

echo "== workflow safe-default contracts =="
WF="$ROOT_DIR/.github/workflows/pg18-parity.yml"
CIT="$ROOT_DIR/.github/workflows/ci.yml"
if [[ -f "$WF" ]]; then
  # The lane must never auto-run on shared branches or ordinary PRs.
  for forbidden in "pull_request:" "schedule:" "cron:"; do
    if grep -qE "^[[:space:]]*[-]?[[:space:]]*${forbidden}" "$WF"; then
      printf '  FAIL workflow contains %s\n' "$forbidden"; fail_count=$((fail_count + 1))
    else
      printf '  ok   workflow has no %s trigger\n' "$forbidden"; pass=$((pass + 1))
    fi
  done
  if grep -A3 "^  push:" "$WF" | grep -q '"ci/postgres18-\*\*"'; then
    printf '  ok   push trigger is limited to the lane prefix\n'; pass=$((pass + 1))
  else
    printf '  FAIL push trigger is not limited to the lane prefix\n'; fail_count=$((fail_count + 1))
  fi
  if grep -q "default: guards-only" "$WF"; then
    printf '  ok   default leg selection is guards-only\n'; pass=$((pass + 1))
  else
    printf '  FAIL default leg selection is not guards-only\n'; fail_count=$((fail_count + 1))
  fi
  # The PG15 control leg must reuse the exact pin from ci.yml.
  if [[ -f "$CIT" ]]; then
    cit_pin="$(grep -m1 -o 'postgres:15@sha256:[0-9a-f]\{64\}' "$CIT")"
    if [[ -n "$cit_pin" ]] && grep -qF "$cit_pin" "$WF"; then
      printf '  ok   PG15 control leg reuses the ci.yml pin\n'; pass=$((pass + 1))
    else
      printf '  FAIL PG15 control leg does not reuse the ci.yml pin\n'; fail_count=$((fail_count + 1))
    fi
    if grep -q "postgres:18@sha256:[0-9a-f]\{64\}" "$WF"; then
      printf '  ok   PG18 image is digest-pinned\n'; pass=$((pass + 1))
    else
      printf '  FAIL PG18 image is not digest-pinned\n'; fail_count=$((fail_count + 1))
    fi
  fi
  if grep -qE "^[[:space:]]*contents: read" "$WF"; then
    printf '  ok   workflow permissions are read-only\n'; pass=$((pass + 1))
  else
    printf '  FAIL workflow permissions are not read-only\n'; fail_count=$((fail_count + 1))
  fi
else
  printf '  FAIL workflow file missing\n'; fail_count=$((fail_count + 1))
fi

echo
printf '%s\n' "pg18-parity-gate.test.sh: $pass passed, $fail_count failed"
[[ "$fail_count" -eq 0 ]] || exit 1
