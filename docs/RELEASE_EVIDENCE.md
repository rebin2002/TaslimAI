# Release evidence and rollback correlation

The `Taslim Release Evidence` workflow produces a secret-free `taslim-release-evidence.json` artifact for pull requests and pushes to `main` or `parallel/**`. It is an additional fail-closed CI gate; it does not deploy, contact production, apply migrations, activate providers, or change billing state. The timestamp and workflow run metadata are diagnostic only; the source, build-input, and migration fingerprints are stable for the same checked-out revision and inputs.

## What the manifest proves

The manifest binds a candidate to:

- the full checked-out commit SHA and commit timestamp;
- the workflow run identity when running in GitHub Actions;
- the exact hashes of the production Dockerfiles, API project file, frontend lockfile, and production configuration;
- a stable fingerprint of the checked-in EF migration files and the latest primary migration;
- the Node and .NET toolchain versions declared by CI;
- the base-image references used by the deployable Dockerfiles; and
- the production safety defaults that must remain true: migrations enabled, persistent S3-compatible storage selected, customer charging disabled, and OpenAI activation disabled.

The manifest contains no connection strings, credentials, API keys, access tokens, or customer data. The script rejects a workflow when `GITHUB_SHA` does not match the checked-out commit, required release inputs are missing or untracked, the migration chain is absent, or a protected production default is unsafe.

## Rollback use

For an incident, retain the evidence artifact alongside the deployment record. Compare the deployed candidate's `source.commitSha`, `buildInputs.fingerprintSha256`, and `migrations.fingerprintSha256` with the candidate selected for rollback. A different migration fingerprint means the database posture must be reviewed before changing application code; do not infer that a code rollback is database-safe from image identity alone.

This artifact is provenance and diagnosis evidence, not a rollback command. Rollbacks continue through the normal reviewed deployment process. Never reset, drop, or recreate a production database as part of this workflow.

## Local validation

```bash
bash scripts/release-evidence.test.sh
bash scripts/release-evidence.sh /tmp/taslim-release-evidence.json
```
