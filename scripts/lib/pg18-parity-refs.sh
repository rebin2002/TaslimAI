#!/usr/bin/env bash
# Ref/context resolution for the disposable PostgreSQL 18 parity harness.
#
# Issue #734 requires a production-matched (PostgreSQL 18) qualification lane
# without changing the behaviour of the existing PostgreSQL 15 control lanes in
# .github/workflows/ci.yml. This helper is therefore an *opt-in* classifier:
# the PG18 lane may only execute when a human explicitly asks for it, and every
# other context must fail closed so an ordinary push or PR cannot silently
# start spending free CI minutes or point a test run at a non-disposable
# database.
#
#   1. workflow_dispatch on the harness branch          -> explicit opt-in
#   2. push to a branch whose name starts with the      -> explicit opt-in
#      declared harness branch prefix
#   3. pull_request whose base or head matches the      -> explicit opt-in
#      declared harness branch
#   4. an interactive local run that sets               -> explicit opt-in
#      PG18_PARITY_LOCAL_OPT_IN=1 (never implied)
#   5. anything else                                    -> refused (fail closed)
#
# The classifier prints the resolved context on stdout and returns 0 when the
# lane may run, 1 (with a diagnostic on stderr) otherwise. It performs no
# network access and mutates nothing, so it can be unit-tested offline.
#
# Context 4 exists so an engineer can qualify a *composed candidate* (for
# example the #718 + #720 pair) on a throwaway database before any hosted run.
# It must be typed by a human: no workflow in this repository sets it.

# Exact ref names / prefixes the lane is allowed to run on. Deliberately
# narrow: no wildcards over product branches.
pg18_expected_branch() { printf '%s\n' "ci/postgres18-parity-harness-20261113"; }
pg18_branch_prefix() { printf '%s\n' "ci/postgres18-"; }

# Explicit human intent for an interactive local run. Deliberately an
# environment variable rather than a default: its absence means "refused".
pg18_local_opt_in="${PG18_PARITY_LOCAL_OPT_IN:-0}"

# pg18_classify_ref <event> <ref> <base_ref> <head_ref> <current_branch>
pg18_classify_ref() {
  local event_name="${1:-}"
  local github_ref="${2:-}"
  local base_ref="${3:-}"
  local head_ref="${4:-}"
  local current_branch="${5:-}"
  local expected_branch prefix
  expected_branch="$(pg18_expected_branch)"
  prefix="$(pg18_branch_prefix)"

  case "$event_name" in
    workflow_dispatch)
      if [[ "$github_ref" == "refs/heads/$expected_branch" ]]; then
        printf '%s\n' "dispatch-opt-in"
        return 0
      fi
      printf 'PG18 parity lane refused: workflow_dispatch from %s is not the harness branch.\n' "${github_ref:-<empty>}" >&2
      return 1
      ;;
    push)
      if [[ "$github_ref" == refs/heads/*"$prefix"* ]]; then
        printf '%s\n' "push-opt-in"
        return 0
      fi
      printf 'PG18 parity lane refused: push ref %s is outside the "%s" prefix.\n' "${github_ref:-<empty>}" "$prefix" >&2
      return 1
      ;;
    pull_request)
      # GitHub checks out a detached refs/pull/<n>/merge ref for pull_request
      # workflows, so branch identity must come from BASE/HEAD, never from the
      # checked-out ref alone.
      if [[ "$base_ref" == "$expected_branch" || "$head_ref" == "$expected_branch" ]]; then
        printf '%s\n' "pull-request-opt-in"
        return 0
      fi
      printf 'PG18 parity lane refused: pull request base=%s head=%s does not involve the harness branch.\n' "${base_ref:-<empty>}" "${head_ref:-<empty>}" >&2
      return 1
      ;;
    "")
      # No CI event context: a local run. A composed candidate is not on the
      # harness branch, so explicit human intent is required, and the
      # disposable-database guards further down still have to pass.
      if [[ "$pg18_local_opt_in" == "1" ]]; then
        printf '%s\n' "local-opt-in"
        return 0
      fi
      printf 'PG18 parity lane refused: local invocation on branch %s requires PG18_PARITY_LOCAL_OPT_IN=1.\n' "${current_branch:-<detached/unknown>}" >&2
      return 1
      ;;
    *)
      printf 'PG18 parity lane refused: unsupported event %s.\n' "$event_name" >&2
      return 1
      ;;
  esac
}

# Disposable-database naming: every database the lane may create or migrate
# must carry an explicit disposable prefix. Anything else is refused, which is
# what keeps "no migrations on real databases" enforceable rather than
# aspirational. The variable is deliberately not called `name`: bash expands
# the command word through `$name`, so `local name` would break execution.
pg18_is_disposable_db_name() {
  local candidate="${1:-}"
  [[ ${#candidate} -le 63 ]] || return 1
  [[ "$candidate" =~ ^taslim_pg18_[A-Za-z0-9][A-Za-z0-9_-]*$ || \
     "$candidate" =~ ^taslim-pg18-[A-Za-z0-9][A-Za-z0-9_-]*$ ]]
}

# SQL is constructed only from database names accepted by the strict
# disposable-name contract above. Quoting remains explicit at every SQL site.
pg18_sql_identifier() {
  local candidate="${1:-}"
  pg18_is_disposable_db_name "$candidate" || {
    printf 'PG18 parity lane refused: database name is not a safe disposable SQL identifier.\n' >&2
    return 1
  }
  printf '"%s"\n' "$candidate"
}

# The same restricted character set makes this SQL literal unambiguous.
pg18_sql_literal() {
  local candidate="${1:-}"
  pg18_is_disposable_db_name "$candidate" || {
    printf 'PG18 parity lane refused: database name is not a safe disposable SQL literal.\n' >&2
    return 1
  }
  printf "'%s'\n" "$candidate"
}

# Accept exactly one canonical Npgsql-style connection string containing each
# of Host, Port, Database, Username, and Password once. Aliases, duplicate
# keys, whitespace/quoting, URI syntax, and unknown fields are rejected rather
# than letting psql and Npgsql interpret the same input differently. The
# returned string is normalized to a fixed field order and is the only form
# the harness may pass to either client.
pg18_canonical_connection() {
  local conn="${1:-}" part key value host port db username password port_number
  local -a parts=()
  local -A fields=()
  if [[ -z "$conn" ]]; then
    printf 'PG18 parity lane refused: empty connection string.\n' >&2
    return 1
  fi
  if [[ "$conn" == *[[:cntrl:]]* ]]; then
    printf 'PG18 parity lane refused: connection string contains a control character.\n' >&2
    return 1
  fi
  if [[ "$conn" == *';' ]]; then
    printf 'PG18 parity lane refused: trailing empty connection field.\n' >&2
    return 1
  fi
  IFS=';' read -r -a parts <<< "$conn"
  if [[ ${#parts[@]} -ne 5 ]]; then
    printf 'PG18 parity lane refused: connection must contain exactly five supported fields.\n' >&2
    return 1
  fi
  for part in "${parts[@]}"; do
    if [[ "$part" =~ ^(Host|Port|Database|Username|Password)=([^;[:space:]]+)$ ]]; then
      key="${BASH_REMATCH[1]}"
      value="${BASH_REMATCH[2]}"
    else
      printf 'PG18 parity lane refused: unsupported connection field or syntax.\n' >&2
      return 1
    fi
    if [[ -n "${fields[$key]+present}" ]]; then
      printf 'PG18 parity lane refused: duplicate connection field.\n' >&2
      return 1
    fi
    fields["$key"]="$value"
  done
  for key in Host Port Database Username Password; do
    if [[ -z "${fields[$key]+present}" ]]; then
      printf 'PG18 parity lane refused: a required connection field is missing.\n' >&2
      return 1
    fi
  done

  host="${fields[Host]}"
  port="${fields[Port]}"
  db="${fields[Database]}"
  username="${fields[Username]}"
  password="${fields[Password]}"
  case "$host" in
    localhost | 127.0.0.1 | host.docker.internal | postgres | pg15 | pg18 | db) ;;
    *)
      printf 'PG18 parity lane refused: host is not a disposable local/service host.\n' >&2
      return 1
      ;;
  esac
  if [[ ! "$port" =~ ^[0-9]{1,5}$ ]]; then
    printf 'PG18 parity lane refused: port is not a valid decimal port.\n' >&2
    return 1
  fi
  port_number=$((10#$port))
  if (( port_number < 1 || port_number > 65535 )); then
    printf 'PG18 parity lane refused: port is outside the valid TCP range.\n' >&2
    return 1
  fi
  if ! pg18_is_disposable_db_name "$db"; then
    printf 'PG18 parity lane refused: database name lacks the disposable prefix or contains unsupported identifier characters.\n' >&2
    return 1
  fi
  if [[ ! "$username" =~ ^[A-Za-z_][A-Za-z0-9_-]{0,62}$ ]]; then
    printf 'PG18 parity lane refused: username contains unsupported characters.\n' >&2
    return 1
  fi
  if [[ ! "$password" =~ ^[A-Za-z0-9._~-]{1,128}$ ]]; then
    printf 'PG18 parity lane refused: password contains unsupported connection-string characters.\n' >&2
    return 1
  fi

  printf 'Host=%s;Port=%s;Database=%s;Username=%s;Password=%s\n' \
    "$host" "$port_number" "$db" "$username" "$password"
}

# Connection field access returns values only from a fully validated canonical
# connection string; callers cannot accidentally revive the old first-match
# parser.
pg18_conn_field() {
  local canonical field="${2:-}"
  case "$field" in Host | Port | Database | Username | Password) ;;
    *) printf 'PG18 parity lane refused: unsupported connection field lookup.\n' >&2; return 1 ;;
  esac
  canonical="$(pg18_canonical_connection "${1:-}")" || return 1
  printf '%s\n' "$canonical" | tr ';' '\n' | sed -n "s/^${field}=//p"
}

# Reject any connection string whose database is not disposable or whose host
# is not loopback / a CI service container. Production hosts (Railway, managed
# Postgres) never match, so an accidentally inherited DATABASE_URL fails the
# run instead of migrating a real database.
pg18_assert_disposable_connection() {
  pg18_canonical_connection "${1:-}" >/dev/null
}
