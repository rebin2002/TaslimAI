# Movie Production QC Preview API

The Movie Studio exposes a provider-neutral QC review preview at:

```http
POST /api/movie-studio/projects/{movieProjectId}/production-qc/preview
```

The endpoint requires authentication, Movie project view permission, and the normal antiforgery token. It evaluates the existing `MovieProductionQcRequest` contract and returns the deterministic `MovieProductionQcDecision` with bounded reason-coded findings.

This is a **read-only preview**. It does not persist a QC decision or evidence, create a Generation Job, reserve or charge usage, publish an Asset, or call an external provider. A future execution workflow may persist the decision after obtaining validated media-inspection evidence and explicit user approval.

The endpoint deliberately returns only provider-neutral fields: contract version, action, review flag, findings, measured/target resolution, recommended source resolution, and estimated additional cost when supplied by the caller.
