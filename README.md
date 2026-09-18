# Taslim.ai

Taslim.ai is a multilingual AI platform foundation designed to make professional AI capabilities simple, fast, and approachable. **Batch 1 establishes the production-ready web/API monorepo shell only.** AI providers, chat execution, accounts, billing, credits, and generation engines are intentionally out of scope.

## Architecture

```text
Taslim Web (Next.js)
        |
        v
Taslim API (ASP.NET Core)
        |
        +---- PostgreSQL (EF Core / Npgsql)
```

The browser owns presentation and navigation. Future AI functionality will be accessed through Taslim API and its server-side orchestration layers; provider secrets must never be sent to browser clients. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Repository structure

```text
apps/
  web/                  Next.js App Router frontend
  api/                  ASP.NET Core Web API
packages/
  contracts/            Reserved for shared schemas/contracts
 docs/
  ARCHITECTURE.md       System boundaries and extension path
```

## Requirements

- Node.js 22+
- npm 10+
- .NET SDK 8.0+
- PostgreSQL 15+ for local database work (the Batch 1 health endpoint does not require an active database connection)

## Local development

Clone the repository and install the frontend dependencies:

```bash
git clone https://github.com/rebin2002/TaslimAI.git
cd TaslimAI
cd apps/web
npm install
```

Copy the examples and adjust values for your environment. Do not commit `.env` files:

```bash
cp .env.example .env.local                 # from apps/web
cp ../api/.env.example ../api/.env          # optional; ASP.NET reads environment variables
```

### Frontend

```bash
cd apps/web
npm run dev
```

The web app runs at `http://localhost:3000`. Set `NEXT_PUBLIC_API_URL` to the API base URL when a browser API client is introduced.

Available frontend checks:

```bash
npm run lint
npm run build
```

### Backend

```bash
cd apps/api
dotnet restore
dotnet run --urls http://localhost:5000
```

The API runs at `http://localhost:5000` and exposes:

```bash
curl http://localhost:5000/health
# {"status":"healthy","service":"Taslim API"}
```

Swagger is available in the Development environment at `/swagger`.

### PostgreSQL configuration

The API reads the database connection string from `ConnectionStrings__Postgres` (or `DATABASE_URL` as a deployment-friendly fallback). For local development, a typical value is:

```text
Host=localhost;Port=5432;Database=taslim;Username=taslim;Password=change-me
```

Batch 1 registers `TaslimDbContext` with Npgsql but intentionally creates no product schema or migrations yet. Future batches should add entities deliberately rather than pre-building the full platform database.

## Localization

The web shell includes English (`en`), Arabic (`ar`), and Kurdish Sorani (`ku`) resources. The language selector updates `html[lang]`, `html[dir]`, and persists the selection locally. Arabic and Kurdish Sorani use RTL layout; the interface uses logical layout patterns and explicit RTL-safe rules where needed.

## Railway deployment

Create three Railway services in the same repository: **Taslim Web**, **Taslim API**, and a managed **PostgreSQL** service.

### Taslim Web service

- Root directory: `/apps/web`
- Build command: `npm ci && npm run build`
- Start command: `npm run start`
- Required variable: `NEXT_PUBLIC_API_URL=https://<taslim-api-domain>`
- Port: Railway-provided `PORT` (Next.js reads it automatically)

The included `apps/web/Dockerfile` is an alternative deployment path and runs the standard Next.js production server.

### Taslim API service

- Root directory: `/apps/api`
- Build command: `dotnet publish -c Release -o ./publish`
- Start command: `dotnet ./publish/Taslim.Api.dll --urls http://0.0.0.0:$PORT`
- Required variables:
  - `ASPNETCORE_ENVIRONMENT=Production`
  - `ConnectionStrings__Postgres=${{Postgres.DATABASE_URL}}` (use the exact Railway reference exposed by the PostgreSQL service)
  - `AllowedOrigins__0=https://<taslim-web-domain>`
- Port: Railway-provided `PORT`

The included `apps/api/Dockerfile` exposes port 8080 and is suitable for a Docker-based Railway service. Do not hard-code localhost in production. The API `/health` endpoint is the service health check.

### PostgreSQL service

Use Railway's managed PostgreSQL service and connect it to the API through a private service reference. Never commit the connection string or any database credentials.

## Batch 1 scope

Included: responsive Taslim application shell, brand placeholder, data-driven department carousels, mobile bottom navigation, placeholder routes, localization/RTL architecture, ASP.NET Core health endpoint, EF Core/Npgsql registration, CORS configuration, environment examples, Dockerfiles, and architecture documentation.

Not included: authentication, AI providers, AI chat execution, image/movie/voice/music generation, credits, subscriptions, payments, admin, or the complete database schema.
