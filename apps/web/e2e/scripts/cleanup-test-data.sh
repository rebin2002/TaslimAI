#!/usr/bin/env bash
set -euo pipefail

: "${E2E_DATABASE_URL:?Set E2E_DATABASE_URL to a disposable PostgreSQL database}"
: "${E2E_ALLOW_DATABASE_CLEANUP:?Set E2E_ALLOW_DATABASE_CLEANUP=true to confirm cleanup}"

if [[ "${E2E_ALLOW_DATABASE_CLEANUP}" != "true" ]]; then
  echo "Refusing cleanup: E2E_ALLOW_DATABASE_CLEANUP must equal true." >&2
  exit 1
fi

# Require an unmistakably disposable database name before issuing DELETE statements.
database_name="$(printf '%s\n' "${E2E_DATABASE_URL}" | sed -E 's#^.*/([^/?]+)(\?.*)?$#\1#; s#.*Database=([^;]+).*#\1#')"
if [[ ! "${database_name,,}" =~ (test|e2e|ci) ]]; then
  echo "Refusing cleanup: database name '${database_name}' is not marked test/e2e/ci." >&2
  exit 1
fi

command -v psql >/dev/null 2>&1 || { echo "psql is required for cleanup." >&2; exit 1; }

psql "${E2E_DATABASE_URL}" --set=ON_ERROR_STOP=1 <<'SQL'
DELETE FROM "AspNetUsers"
WHERE "Email" LIKE 'e2e+%@example.test';
SQL

echo "Removed disposable E2E users from ${database_name}."
