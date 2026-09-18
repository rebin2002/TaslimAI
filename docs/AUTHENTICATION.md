# Taslim.ai Batch 2 authentication and ownership

## Identity architecture

Taslim uses ASP.NET Core Identity with a GUID-backed `ApplicationUser`. Identity owns normalized email, password hashing, security stamps, lockout fields, and other security fields. Taslim-owned profile fields are `DisplayName`, `PreferredLanguage`, `CreatedAt`, `UpdatedAt`, `LastLoginAt`, and `IsActive`.

Registration uses `POST /api/auth/register` and creates the user, a Personal Workspace, and an Owner membership in one EF transaction. The user is signed in only after the workspace and membership are saved. Login uses `POST /api/auth/login`; authentication failures use the generic message `Invalid email or password.`. Current session data is returned by `GET /api/auth/me`, and `POST /api/auth/logout` ends the cookie session.

The server never returns password or Identity security fields. No access token is stored in localStorage, and the frontend never receives a provider or database secret.

## Registration password validation

The registration form reads `GET /api/auth/password-policy`, which is generated from the configured `IdentityOptions.Password` values. The current policy is:

| Requirement | Current value |
| --- | --- |
| Minimum length | 10 characters |
| Uppercase character | Required |
| Lowercase character | Required |
| Digit | Required |
| Non-alphanumeric character | Required |
| Unique characters | 1 or more, Identity default |

The form displays these requirements in English, Arabic, and Kurdish Sorani and updates each rule as the user types. The server maps safe Identity password-validator codes into `error.fields.password` values such as `PASSWORD_TOO_SHORT` and `PASSWORD_REQUIRES_DIGIT`. Other registration failures remain generic to avoid exposing account-enumeration or infrastructure details.

## Cookie authentication and CORS

The API issues an HttpOnly Identity cookie named `taslim.auth`. In Production, it is `Secure` and `SameSite=None` so the separately hosted Railway Web and API origins can make credentialed requests. In local development, it is `SameSite=Lax` and follows the request security scheme.

The frontend API client sends `credentials: "include"` for every request. The API only allows origins from `AllowedOrigins`; it uses `AllowCredentials()` and never combines credentials with a wildcard origin.

Current Railway configuration:

```text
AllowedOrigins__0=https://taslim-web-production.up.railway.app
```

When custom domains are introduced, set it to the exact web origin:

```text
AllowedOrigins__0=https://taslim.ai
```

The API origin changes from the Railway URL to `https://api.taslim.ai` in frontend `NEXT_PUBLIC_API_URL`; the cookie remains host-scoped to the API and still works with credentialed requests.

## Railway forwarded HTTPS

Railway terminates public TLS at its reverse proxy and forwards the request to the API over the internal service network. The API therefore receives an internal HTTP connection even when the browser used HTTPS. Without forwarded-header processing, `HttpContext.Request.IsHttps` remains false and Production antiforgery correctly refuses to issue its Secure cookie.

Taslim configures `ForwardedHeadersMiddleware` with this exact policy:

```csharp
options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
options.ForwardLimit = 1;
```

The middleware is registered before exception handling, CORS, authentication, authorization, and controller execution. It consumes only `X-Forwarded-Proto`; Taslim does not consume forwarded client IP or host values for identity or CSRF decisions. In Production, the application allows the single Railway ingress hop because Railway’s public ingress source range is not a stable per-service application setting. If the deployment later gains a fixed private proxy, operators can provide explicit addresses through `ForwardedHeaders:KnownProxies` and the middleware will use those configured addresses.

This preserves `CookieSecurePolicy.Always` for the authentication and antiforgery cookies. It does not disable CSRF, make cookies insecure, or trust multiple forwarded hops. Local development continues to use direct HTTP and `SameAsRequest` cookie policies.

## CSRF flow

Cookie authentication makes browser state-changing requests vulnerable to cross-site request forgery. Taslim uses ASP.NET Core antiforgery tokens:

1. The frontend calls `GET /api/auth/csrf` with credentials.
2. The API sets the non-HttpOnly `taslim.csrf` cookie and returns the request token.
3. The frontend sends that token in the `X-CSRF-TOKEN` header for registration, login, logout, profile updates, project creation, updates, archive, and restore.
4. ASP.NET Core validates the cookie/header pair through `[ValidateAntiForgeryToken]`.

The CSRF cookie is `Secure` and `SameSite=None` in Production. The token is not an authentication credential; it is only a request-integrity token.

## Workspace ownership model

```text
ApplicationUser
  └── WorkspaceMember ── Workspace
                           └── Project
```

Projects belong to Workspaces rather than directly to users. `WorkspaceMember` stores the relationship and role (`Owner`, `Admin`, or `Member`) and has a unique `(WorkspaceId, UserId)` constraint. Every newly registered user receives one Personal Workspace and an Owner membership.

The reusable `WorkspaceAccessService` checks membership before list, create, read, update, archive, and restore operations. A browser-supplied workspace ID is never accepted as proof of access. Cross-user project reads and writes return `403` without exposing private project data.

## Project lifecycle

Projects are created through `POST /api/workspaces/{workspaceId}/projects` and are updated through `PATCH /api/projects/{projectId}`. Archive and restore are explicit state transitions. Archived projects remain in the database and are filtered into the Archived view; there is no permanent delete endpoint in Batch 2.

## Database and migrations

The initial migration is `InitialIdentityWorkspacesProjects`. It creates the ASP.NET Core Identity tables plus `Workspaces`, `WorkspaceMembers`, and `Projects`, with foreign keys, unique workspace slugs, membership uniqueness, and workspace/status indexes.

Create or update migrations from the repository root:

```bash
dotnet tool restore
dotnet tool run dotnet-ef migrations add <MigrationName> \
  --project apps/api --startup-project apps/api \
  --output-dir Persistence/Migrations
```

Apply migrations locally against an explicitly selected development database:

```bash
Database__ApplyMigrations=true \
ConnectionStrings__Postgres='Host=localhost;Port=5432;Database=taslim;Username=taslim;Password=change-me' \
ASPNETCORE_ENVIRONMENT=Production \
dotnet run --project apps/api
```

For Railway, the API Docker deployment uses `ASPNETCORE_ENVIRONMENT=Production` and `Database__ApplyMigrations=true` from `appsettings.Production.json`. The API acquires a PostgreSQL advisory lock before applying pending migrations, logs a critical failure, and stops if migration fails. It never calls `EnsureCreated()` and never resets or drops production data.

Railway PostgreSQL may provide a URI such as `postgresql://user:password@host:port/database`. The API normalizes this server-side to an Npgsql connection string and requires SSL for URI-based production connections. Credentials are never logged or returned.

## API error contract

Controlled errors use this shape:

```json
{
  "error": {
    "code": "PROJECT_NOT_FOUND",
    "message": "Project not found."
  }
}
```

Production exceptions return a generic `INTERNAL_ERROR` response. Stack traces, SQL, connection details, and secrets are not returned to clients.
