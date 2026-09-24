# End-to-End Testing

Taslim.ai uses [Playwright](https://playwright.dev/) for browser-level end-to-end coverage. The suite runs against the real Next.js browser application and ASP.NET Core API, using the API's normal cookie session and CSRF protections. It does not add a production authentication bypass, test-only production route, seeded credential, or provider-success shortcut.

## Prerequisites

Local execution requires Node.js 22+, npm 10+, the .NET 8 SDK, PostgreSQL 15+, and a Chromium browser installed by Playwright. From the repository root:

```bash
npm ci
npx playwright install chromium
```

The API must be configured against a disposable local or CI database. Do not point the suite at a production database.

## Start the test environment

Create a disposable PostgreSQL database and start the API with charging and external AI providers disabled:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Postgres='Host=localhost;Port=5432;Database=taslim_e2e;Username=taslim;Password=change-me'
export Database__ApplyMigrations=true
export AllowedOrigins__0=http://127.0.0.1:3000
export Ai__OpenAI__Enabled=false
export Files__StorageProvider=Local
export Files__LocalRootPath="$PWD/.e2e-files"
dotnet run --project apps/api --urls http://127.0.0.1:5000
```

In another terminal, start the frontend with the API URL embedded in the browser bundle:

```bash
export NEXT_PUBLIC_API_URL=http://127.0.0.1:5000
npm run dev --workspace @taslim/web -- --hostname 127.0.0.1
```

The Playwright config starts the web server automatically when `E2E_WEB_URL` is not set. The API remains an explicit prerequisite because database and API lifecycle must be controlled by the local developer or CI job.

## Commands

Run all E2E projects (desktop Chromium, 390×844 mobile Chromium, and RTL Chromium):

```bash
npm run test:e2e --workspace @taslim/web
```

Run one suite or project:

```bash
npx playwright test e2e/auth.spec.ts --config apps/web/playwright.config.ts
npx playwright test --project=mobile-chromium --config apps/web/playwright.config.ts
npx playwright test --project=rtl-chromium --config apps/web/playwright.config.ts
```

Useful local commands are:

```bash
npm run test --workspace @taslim/web
npm run lint --workspace @taslim/web
npx tsc --noEmit -p apps/web/tsconfig.json
npm run build --workspace @taslim/web
```

The API integration suite remains:

```bash
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj
```

## Environment variables

| Variable | Default | Purpose |
| --- | --- | --- |
| `E2E_WEB_URL` | `http://127.0.0.1:3000` | Existing frontend URL. Setting it disables Playwright's automatic web-server startup. |
| `E2E_API_URL` | `NEXT_PUBLIC_API_URL` or `http://127.0.0.1:5000` | API origin used by the CSRF-aware fixture helpers. |
| `E2E_TEST_PASSWORD` | `E2eStrongPassword!123` | Password for unique disposable users. Override in CI if the Identity policy changes; do not use a production password. |
| `NEXT_PUBLIC_API_URL` | `http://127.0.0.1:5000` for the auto-started web server | API origin compiled into the local Next.js browser bundle. |
| `ConnectionStrings__Postgres` | none | Disposable API database connection string. |
| `Database__ApplyMigrations` | `false` | Set to `true` only for the disposable E2E database. |
| `Ai__OpenAI__Enabled` | application default | Keep `false` for E2E. The suite uses the provider-independent `system.test` generation job and intercepts only one browser error scenario. |
| `Files__StorageProvider` | application default | Use `Local` with a disposable `Files__LocalRootPath` for attachment coverage. |

No secrets belong in repository files. CI should inject database passwords through the runner's secret/environment mechanism.

## Test-user lifecycle and isolation

Each browser test creates a unique email in the `e2e+...@example.test` namespace through the normal registration UI. The fixture completes onboarding through the visible onboarding dialog, then logs out in teardown where the test has not already done so. Tests use separate users by default and never rely on a pre-existing account or production credential.

API-backed setup in the fixtures uses the authenticated browser context's cookie jar and obtains CSRF tokens from `GET /api/auth/csrf`. It is limited to deterministic workspace records such as projects and conversations. No helper changes API authorization or impersonates a user.

For local and CI runs, the preferred cleanup is disposal of the whole database and local file directory. If a shared disposable database must be cleaned, use the guarded script only after confirming that its database is a test database:

```bash
E2E_DATABASE_URL='Host=localhost;Port=5432;Database=taslim_e2e;Username=taslim;Password=change-me' \
E2E_ALLOW_DATABASE_CLEANUP=true \
./apps/web/e2e/scripts/cleanup-test-data.sh
```

The script refuses to run unless both the explicit approval variable and a database name containing `test`, `e2e`, or `ci` are present. It deletes only the `e2e+%@example.test` user namespace. It is not a production data-cleanup tool.

## Provider and charging controls

The API is started with OpenAI and other optional production providers disabled. The system test job (`jobType=system.test`) is the existing provider-independent deterministic job path and is safe for lifecycle, terminal-state, and idempotency assertions. The chat provider-failure test intercepts only the browser's stream request and returns a controlled test `503`; this interception is test code, not application code. No provider success is fabricated in production code.

Billing assertions verify the read-only charging-disabled state. The E2E suite does not configure payment credentials, create a checkout, charge a customer, or activate customer billing.

## Coverage map

| Suite | Coverage |
| --- | --- |
| `auth.spec.ts` | Registration, login, logout, onboarding, existing-user onboarding bypass, authenticated home access |
| `workspace.spec.ts` | Project creation, project detail, Studio handoff with project preselection, project-to-Chat context, Assets access isolation, account/settings, charging-disabled billing, normal-user Admin Operations denial |
| `chat.spec.ts` | Start Chat, persistent conversation navigation, rename, archive/delete, file attachment, project context, controlled provider error presentation |
| `navigation.spec.ts` | Global Search, Notifications unread/read controls, Activity navigation |
| `generation.spec.ts` | Provider-independent job creation, terminal states, safe errors, idempotent job creation |
| `mobile.spec.ts` | 390×844 Home, Projects, Create, Activity/Notifications, and Account bottom navigation; order is Home, Projects, Create, Activity, Account |
| `rtl.spec.ts` | Arabic RTL direction, language, navigation, focus, and overflow smoke checks |
| `accessibility.spec.ts` | Serious/critical axe checks on Home and Projects plus labeled auth controls and keyboard focus |

Selectors prefer roles, accessible names, labels, URLs, and existing semantic structure. No pixel coordinates or brittle CSS selectors are used for behavior assertions; the two CSS locators in the attachment/rename helpers target existing native controls that do not currently expose a more specific accessible name.
