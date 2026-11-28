#!/usr/bin/env bash
# Rehearse introducing the older Movie decision migration after the deployed academic migration.
set -euo pipefail
base=${PR_BASE_SHA:?}
head=${PR_HEAD_SHA:?}
migration=20261005160000_AddMovieMissingInsertDecisions
retained_migration=20261127200658_RecordProviderFinalizationOutcome
path="apps/api/Persistence/Migrations/$migration.cs"
[[ $base =~ ^[0-9a-f]{40}$ && $head =~ ^[0-9a-f]{40}$ ]]
git cat-file -e "$base^{commit}"
git cat-file -e "$head^{commit}"
if git cat-file -e "$base:$path" 2>/dev/null || ! git cat-file -e "$head:$path" 2>/dev/null; then
  echo "This PR does not introduce the Movie decision migration."
  exit 0
fi

# Every connection below points at the local, digest-pinned service container in this job.
db=taslim_movie_upgrade
connection="Host=127.0.0.1;Port=5432;Database=$db;Username=taslim;Password=change-me"
export ConnectionStrings__Postgres="$connection" Database__ApplyMigrations=false
psql_in() {
  docker exec -i "${POSTGRES_SERVICE_ID:?}" psql -X -qAt -v ON_ERROR_STOP=1 -U taslim "$@"
}
sql() { psql_in -d "$db" "$@"; }
history() { sql -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" COLLATE "C"'; }
fixture() {
  sql <<'SQL'
SELECT row_to_json(w) FROM "Workspaces" w WHERE "Id"='00000000-0000-4000-8000-000000000001';
SELECT row_to_json(a) FROM "AcademicEvidenceAuditEvents" a WHERE "SourceId"='00000000-0000-4000-8000-000000000002';
SQL
}
work=$(mktemp -d)
cleanup() {
  git worktree remove --force "$work/base" 2>/dev/null || true
  git worktree remove --force "$work/candidate" 2>/dev/null || true
  rmdir "$work"
}
trap cleanup EXIT
git worktree add --quiet --detach "$work/base" "$base"
git worktree add --quiet --detach "$work/candidate" "$head"
migrate() (
  cd "$1"
  dotnet tool restore
  dotnet ef database update --project apps/api --startup-project apps/api --connection "$connection"
)

psql_in -d postgres -c "CREATE DATABASE $db"
echo "Apply the PR base $base to the disposable database"
migrate "$work/base"
before=$(history)
grep -Fxq '20261124065935_AddAcademicResearchIntegration' <<<"$before"
grep -Fxq "$retained_migration" <<<"$before"
echo "Verified PR base migration history retains $retained_migration"
if grep -Fxq "$migration" <<<"$before"; then exit 1; fi
[[ $(sql -c "SELECT to_regclass('public.\"MovieMissingInsertDecisions\"') IS NULL") == t ]]
sql <<'SQL'
INSERT INTO "Workspaces" ("Id","Name","Slug","Type","IsArchived","CreatedAt","UpdatedAt")
VALUES ('00000000-0000-4000-8000-000000000001','Upgrade rehearsal','upgrade-rehearsal','Personal',false,now(),now());
INSERT INTO "AcademicEvidenceAuditEvents"
("Sequence","SourceId","WorkspaceId","EventKind","ActorKind","PayloadCanonical","OccurredAtUtc","PreviousHash","EntryHash")
VALUES (1,'00000000-0000-4000-8000-000000000002','00000000-0000-4000-8000-000000000001',
        'upgrade-rehearsal','system','{}',now(),repeat('0',64),repeat('1',64));
SQL
seed=$(fixture)
[[ $(wc -l <<<"$seed") -eq 2 ]]

echo "Upgrade the same database to $head"
migrate "$work/candidate"
expected=$(printf '%s\n' "$before" "$migration" | LC_ALL=C sort)
[[ $(history) == "$expected" && $(fixture) == "$seed" ]]
table_oid=$(sql -c "SELECT 'public.\"MovieMissingInsertDecisions\"'::regclass::oid")
[[ $table_oid =~ ^[0-9]+$ ]]
[[ $(sql -c "SELECT count(*) FROM pg_constraint WHERE conrelid='$table_oid'::oid AND contype='f'") == 4 ]]

echo "Repeat the upgrade and verify the migration history, table identity and original rows"
migrate "$work/candidate"
[[ $(history) == "$expected" && $(fixture) == "$seed" ]]
[[ $(sql -c "SELECT 'public.\"MovieMissingInsertDecisions\"'::regclass::oid") == "$table_oid" ]]
echo "PASS: base and academic rows survived, the Movie decision table was added once, and the repeat was a no-op."
