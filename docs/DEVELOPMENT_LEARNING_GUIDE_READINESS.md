# Development Learning Guide Readiness

**Scope:** provider-neutral route and UX hardening for the Development workspace and its learning guide.

## Delivered in this continuation

The parent Development hub (`/development`) and the workspace-scoped learning guide (`/development/learn`) now use the same defense-in-depth route boundary as the Debug guide:

1. `requireAuthenticatedPage()` runs on the server before protected content can render.
2. `dynamic = "force-dynamic"` prevents a protected route from being treated as static output.
3. `ProtectedPage` remains in place as the client hydration and session-recovery boundary.
4. The server gate keeps the return path local (`/development` or `/development/learn`) through the existing safe redirect helper.

This continuation is based on the existing Development hub work and intentionally does not change the Learn component, its local state model, or any provider/API surface.

## Existing UX and persistence guarantees

The learning guide already provides the following provider-neutral behavior:

- English, Arabic, and Kurdish Sorani copy, including RTL-safe layout styles.
- An accessible loading status while the authenticated workspace namespace hydrates.
- A workspace-unavailable alert with a safe return link.
- A labelled progress bar, keyboard-visible controls, and polite persistence feedback.
- Local-only notes and lesson completion state under a workspace-specific browser-storage key.
- Bounded parsing and note length, duplicate lesson-id removal, malformed-data recovery, and storage-quota failure recovery.
- No API calls, provider calls, billing calls, customer charging, purchases, or external generation.

The browser storage is a convenience cache, not an authorization source. Workspace ownership and authentication remain server/API responsibilities; client state is never sent as proof of access.

## Integration order and overlap

- **PR #91** remains the canonical learning-guide foundation.
- **PR #108** remains the parent Development hub/navigation layer.
- **This continuation** adds server-side route protection to those two routes without changing their feature files.
- **PR #169** adds the build-readiness route and should retain the same server-gate pattern when integrated.
- **PR #198** already applies the server-gate pattern to the Debug guide.

No provider, billing, database, migration, deployment, secret, or production configuration work is part of this change.

## Validation checklist

From `apps/web` or the repository root:

```bash
npm test --workspace @taslim/web -- --run \
  src/app/development/page.test.ts \
  src/app/development/learn/page.test.ts \
  src/lib/serverAuth.test.ts \
  src/lib/developmentLearningState.test.ts \
  src/lib/developmentNavigation.test.ts \
  src/components/DevelopmentHub.test.ts
npm run lint --workspace @taslim/web
npm run typecheck --workspace @taslim/web
NEXT_PUBLIC_API_URL=https://api.invalid.example npm run build --workspace @taslim/web
npm audit --omit=dev --audit-level=high

git diff --check
```

The complete web suite and CI remain authoritative for the full application gate. This branch does not merge `main`, deploy, call a paid provider, charge a customer, alter a database, or bypass an existing safety gate.
