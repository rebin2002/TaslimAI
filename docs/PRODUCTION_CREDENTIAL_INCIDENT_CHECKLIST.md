# Production Credential Incident Checklist

Use this checklist when a production environment-variable or credential compromise is suspected. It is an operational control, not proof that an incident occurred. It must be completed by authorized personnel with access to the Railway project, provider consoles, GitHub audit logs, and the approved secret manager.

## Release hold

- Pause production release, merge, and configuration changes until the incident owner records a decision.
- Do not paste, export, print, or attach secret values. Record only secret names or categories, affected service/environment, timestamps, deployment SHA, and incident identifiers.
- Keep the integration candidate and current production deployment unchanged until the authorized review establishes a safe disposition. This checklist does not authorize merging or releasing PR #275.

## Authorized credential response

- Identify affected variable names by category only: database, object storage, AI/provider, email, webhook signing, OAuth, payment, or internal service credentials.
- Through the approved secret-management process, revoke or rotate every potentially exposed credential. Do not reuse a value that may have been exposed.
- Update the authorized deployment environment with the replacement values using the normal review and change-control process. Never put values in Git, issue comments, CI summaries, build arguments, artifacts, or chat.
- Confirm least privilege, expected scopes, environment separation, expiration, and owner assignment for each replacement credential.
- Review provider and Railway access-control membership. Remove unknown users, stale tokens, unused integrations, and credentials without a current owner.

## Audit and containment evidence

- Review Railway deployment, environment-variable, access, and configuration-change audit logs for the suspected time window.
- Review GitHub audit logs, repository secret-scanning alerts, push-protection events, workflow logs, artifacts, and action summaries for exposure or unauthorized access.
- Review provider authentication and usage logs for unexpected locations, actors, models, storage operations, database access, payment activity, or quota changes. Record metadata and event IDs only.
- Confirm that no secret value appears in repository history, tracked files, Docker build arguments, generated manifests, test output, CI logs, browser bundles, or public artifacts.
- Run the repository-side `scripts/secret-hygiene-audit.sh` check. A pass means only that no supported high-confidence credential literal was found in tracked files; it does not verify Railway or provider runtime state.
- Preserve the non-secret evidence packet and access-log references according to the project’s incident-retention policy.

## Release re-entry criteria

Release work may resume only after the authorized incident owner records that credential revocation/rotation is complete, audit-log review has no unresolved unauthorized activity, affected services have been smoke-tested without exposing values, and required reviewers approve the release path. Production deployment, database writes, provider activation, billing, and customer charging remain separate approvals.
