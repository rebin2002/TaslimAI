# Migration safety and release evidence

Taslim CI runs `Taslim Migration Safety` for pull requests and for pushes to `main` and `parallel/**`. The workflow is a disposable-database check; it never connects to Railway or any production database.

## What the gate proves

The gate performs four checks in order:

1. **Model drift** — `dotnet ef migrations has-pending-model-changes` fails when the checked-in `TaslimDbContextModelSnapshot` does not represent the current EF model.
2. **Fresh application** — the complete migration chain is applied to a clean, pinned PostgreSQL 15 service.
3. **Repeat safety** — the same migration command is run again against the now-current database and must complete as a no-op.
4. **Release evidence** — EF emits an idempotent SQL script, which is uploaded as a workflow artifact with its SHA-256 digest in the job summary.

A green gate therefore covers both the source-model bookkeeping failure mode and the PostgreSQL execution path that a fresh environment would use. The repeat run also catches migrations that do not behave safely when a deployment is restarted after the database has already advanced.

## Migration author workflow

When the data model changes:

```bash
dotnet tool restore
dotnet tool run dotnet-ef migrations add <MigrationName> \
  --project apps/api --startup-project apps/api \
  --output-dir Persistence/Migrations
```

Commit the migration, its designer (when generated), and `TaslimDbContextModelSnapshot.cs` together. Run the API test suite and the migration safety script with a disposable PostgreSQL database before requesting release review.

Do not edit an older applied migration to repair production behavior. Add a forward migration that is safe for the data already present. The `Down` method is useful for local development and test teardown; production rollback is **not** performed by running `Down` against customer data.

## Release and rollback evidence

For each release candidate, retain the migration-safety workflow URL, the idempotent SQL artifact, its SHA-256 digest, and the latest migration identifier from the job output. If a deployment fails after migrations begin:

1. Keep the failed deployment isolated and check `/health/ready` and startup logs.
2. Compare the deployment commit, migration identifier, and artifact digest with the reviewed release candidate.
3. If the application binary is incompatible with the database, stop the rollout and redeploy the last known-good application only when its schema compatibility has been confirmed.
4. Prefer a reviewed forward-fix migration for schema correction. Do not reset, recreate, or manually roll back the production database.
5. After recovery, record the final deployment commit, applied migration identifier, readiness result, and the workflow artifact digest in the incident notes.

A green workflow is evidence that migrations are reproducible against the pinned CI PostgreSQL service. It is not a substitute for reviewing data-volume impact, lock duration, backfill cost, or backward compatibility before a production release.

## Failure interpretation

- **Pending model changes:** add the missing migration or revert the model change; do not bypass the check.
- **Fresh application failure:** inspect the failing migration’s generated SQL and PostgreSQL error; the release is not ready.
- **Repeat-run failure:** the migration chain is not safely restartable; repair it before deployment.
- **Missing or empty SQL artifact:** treat as a failed release-evidence step, even if another command appeared successful.
