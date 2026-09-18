# Taslim.ai

Taslim.ai is a multilingual AI platform foundation designed to make professional AI capabilities simple, fast, and approachable. **Batch 2 adds identity, users, personal workspaces, and projects.** AI providers and generation engines remain intentionally out of scope.

## Architecture

```text
Taslim Web (Next.js)
        |
        | credentialed HTTPS + CSRF header
        v
Taslim API (ASP.NET Core Identity + Web API)
        |
        +---- PostgreSQL (EF Core / Npgsql)
```

The browser owns presentation and navigation. The API owns authentication, authorization, persistence, and future AI orchestration. Provider secrets must never be sent to browser clients. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/AUTHENTICATION.md](docs/AUTHENTICATION.md).

## Repository structure

```text
apps/
  web/                  Next.js App Router frontend
  api/                  ASP.NET Core Web API
  api.Tests/            Identity and ownership integration tests
packages/
  contracts/            Reserved for shared schemas/contracts
 docs/
  ARCHITECTURE.md       System boundaries and extension path
  AUTHENTICATION.md     Cookie, CSRF, ownership, and migration details
```

## Requirements

- Node.js 22+
- npm 10+
- .NET SDK 8.0+
- PostgreSQL 15+ for local database work

## Local development

```bash
git clone https://github.com/rebin2002/TaslimAI.git
cd TaslimAI
cd apps/web
npm install
```

Copy environment examples and adjust values. Never commit `.env` files:

```bash
cp apps/web/.env.example apps/web/.env.local
cp apps/api/.env.example apps/api/.env
```

### Frontend

```bash
npm run dev --workspace @taslim/web
```

The web app runs at `http://localhost:3000`. Set `NEXT_PUBLIC_API_URL=http://localhost:5000` in `apps/web/.env.local`.

Checks:

```bash
npm run lint --workspace @taslim/web
npm run build --workspace @taslim/web
```

### Backend

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet run --project apps/api --urls http://localhost:5000
```

The API runs at `http://localhost:5000`. Health check:

```bash
curl http://localhost:5000/health
# {"status":"healthy","service":"Taslim API"}
```

Swagger is available in Development at `/swagger`.

### PostgreSQL configuration

The API reads `ConnectionStrings__Postgres` or `DATABASE_URL`. It accepts either a standard Npgsql connection string or Railway’s URI format, such as `postgresql://user:password@host:port/database`; URI values are normalized server-side and are never logged.

For local work:

```text
ConnectionStrings__Postgres=Host=localhost;Port=5432;Database=taslim;Username=taslim;Password=change-me
Database__ApplyMigrations=false
```

Batch 2 uses EF Core migrations. It does not call `EnsureCreated()` or reset the database.

## Authentication and security

ASP.NET Core Identity issues an HttpOnly `taslim.auth` cookie. In Production it is Secure and configured for credentialed Web/API requests across the separate Railway origins. The frontend always sends `credentials: "include"` and uses `GET /api/auth/csrf` to obtain a request token for state-changing requests, which is sent through `X-CSRF-TOKEN`.

The API allows only configured origins and uses `AllowCredentials()`; wildcard CORS is not used. Private resources are authorized through Workspace membership. Projects are never directly owned by the browser user, and no authentication token is stored in localStorage.

## Batch 2 product flows

- Register at `/register`
- Sign in at `/login`
- View and update profile at `/account`
- Automatically receive a Personal Workspace
- Create and edit projects at `/projects`
- Open a project at `/projects/[projectId]`
- Archive and restore projects without physical deletion
- View active and archived project lists

Supported project types are General, Movie, Marketing, Business, Research, Education, and Development. These are extensible server-side values, not a closed database enum.

## Migrations

Restore the pinned EF tool:

```bash
dotnet tool restore
```

Create a migration from the repository root:

```bash
dotnet tool run dotnet-ef migrations add <MigrationName> \
  --project apps/api --startup-project apps/api \
  --output-dir Persistence/Migrations
```

The initial migration is `InitialIdentityWorkspacesProjects` and creates ASP.NET Identity tables plus `Workspaces`, `WorkspaceMembers`, and `Projects`.

To apply migrations locally against an explicitly selected database:

```bash
ASPNETCORE_ENVIRONMENT=Production \
Database__ApplyMigrations=true \
ConnectionStrings__Postgres='Host=localhost;Port=5432;Database=taslim;Username=taslim;Password=change-me' \
dotnet run --project apps/api
```

Production startup migrations use a PostgreSQL advisory lock and fail clearly if a migration cannot be applied. Do not run development reset commands against Production.

## Tests

Run the API integration suite:

```bash
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj
```

The suite covers registration, duplicate email, login failure, session/logout, personal workspace ownership, project lifecycle, cross-user authorization, and controlled validation errors. Tests use a relational in-memory SQLite database so registration transactions are exercised realistically.

## Railway deployment

Production infrastructure uses three services: **Taslim Web**, **Taslim API**, and managed **PostgreSQL**.

### Taslim Web

- Root directory: `/apps/web`
- Builder: Dockerfile (recommended and required when using the included multi-stage Docker build)
- Dockerfile path: `Dockerfile`
- Custom build command: empty; the Dockerfile runs `npm ci` and `npm run build`
- Custom start command: empty; the Dockerfile runs `npm run start`
- Required variable: `NEXT_PUBLIC_API_URL=https://taslim-api-production.up.railway.app`
- Port: Railway-provided `PORT`

`NEXT_PUBLIC_API_URL` is a public Next.js variable and is embedded during `next build`. The web Dockerfile explicitly declares it as a Docker `ARG` in the builder stage and promotes it to `ENV` before `npm run build`; Railway injects service variables into Docker builds only when they are declared with `ARG`. Do not add the API URL to application source code. For local development, the Docker build argument defaults to `http://localhost:5000`, matching `apps/web/.env.example`.

In Railway, keep `NEXT_PUBLIC_API_URL` configured on the Taslim Web service and use the Dockerfile builder. No custom build command is required; the Dockerfile performs the build.

### Taslim API

**Preserve Dockerfile deployment. Do not switch the API back to Railpack.**

- Root directory: `/apps/api`
- Builder: Dockerfile
- Dockerfile path: `Dockerfile`
- Custom build command: empty
- Custom start command: empty
- Required variables:

```text
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__Postgres=${{Postgres.DATABASE_URL}}
AllowedOrigins__0=https://taslim-web-production.up.railway.app
```

`appsettings.Production.json` enables `Database__ApplyMigrations=true`. The API Dockerfile uses a multi-stage .NET 8 build and binds to port 8080. Railway should route its provided service port to the container; if the platform requires an explicit variable, set `ASPNETCORE_HTTP_PORTS=8080`.

### PostgreSQL

Use Railway’s managed PostgreSQL service and a private service reference for the API connection string. Do not commit credentials or replace the Railway reference with a hard-coded value. No manual migration command is required after deployment when Production startup migrations are enabled; monitor the first API deployment logs for the migration completion or a clear startup failure.

## Scope boundary

Included in Batch 2: ASP.NET Core Identity, secure cookie sessions, CSRF protection, profile updates, automatic Personal Workspace creation, membership-based authorization, projects, project lifecycle, EF migration, tests, login/register UI, account UI, project UI, localization, RTL preservation, and Railway documentation.

Not included: AI Chat execution, OpenAI, Gemini, Anthropic, AI Core, model routing, generation engines, billing, credits, subscriptions, admin, invitations, teams, business workspace creation, social login, or native mobile apps.
