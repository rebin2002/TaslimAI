/**
 * Mounts the real academic surface in a browser under a principal the test can
 * change while the tree stays mounted.
 *
 * This is the shape the component itself documents as supported - "Optional host
 * override for tests and embedding" - and the shape a later phase reaches when a
 * workspace switcher or an embedding route renders the studio without a fresh
 * document. A same-origin second tab replacing the shared cookie-backed session
 * produces the same in-place principal change through the app's own session
 * revalidation.
 *
 * Two modes, so the fix cannot lean on a single mechanism:
 * - `studio` (default) renders `AcademicResearchStudio`, the shipped container,
 *   whose identity-keyed subtree is the primary protection.
 * - `workspace` renders the inner workspace unit with a reused React instance, so
 *   only the fail-closed principal fence stands between one student's brief and
 *   the next student's session.
 *
 * The module is read through a namespace import so this same harness builds and
 * runs on both sides of the fix. Before it there was no inner unit at all - one
 * container holding state that outlived the principal - so `workspace` mode mounts
 * that same shipped tree, and both modes then cover the identical surface. That is
 * the point of the reproduction: this spec fails on `ec623a4b` and passes on the
 * fixed head.
 */
import { createRoot } from "react-dom/client";
import { useMemo, type ReactElement } from "react";
import * as academicStudio from "@/components/academic/AcademicResearchStudio";
import { useAuth } from "@/components/AuthProvider";
import { AuthProviderStub, setHarnessIdentity, type HarnessIdentity } from "./harnessAuth";
import { LocaleProviderStub } from "./harnessLocale";

declare global {
  interface Window {
    /** Identity present when the harness document first mounts. */
    __academicInitialIdentity?: HarnessIdentity;
    /** Identity API the regression spec drives. */
    __academicHarness?: { setIdentity: (next: HarnessIdentity) => void };
  }
}

type StudioModule = {
  AcademicResearchStudio: (props: { initialState?: unknown }) => ReactElement | null;
  AcademicWorkspace?: (props: { principal: WorkspacePrincipal; initialState?: unknown }) => ReactElement | null;
};

/** Scope the workspace state belongs to; a null id means no signed-in principal. */
type WorkspacePrincipal = { userId: string | null; workspaceId: string | null };

const studio = academicStudio as unknown as StudioModule;
const ShippedContainer = studio.AcademicResearchStudio;
const InnerWorkspace: NonNullable<StudioModule["AcademicWorkspace"]> =
  studio.AcademicWorkspace ?? (() => <ShippedContainer />);

/** Host that changes `principal` without remounting the workspace instance. */
function UnkeyedWorkspaceHost() {
  const { user, workspace } = useAuth();
  const principal = useMemo(
    () => ({ userId: user?.id ?? null, workspaceId: workspace?.id ?? null }),
    [user?.id, workspace?.id],
  );
  return <InnerWorkspace principal={principal} />;
}

function mount(): void {
  const container = document.getElementById("root");
  if (!container) return;
  setHarnessIdentity(window.__academicInitialIdentity ?? null);
  const mode = new URLSearchParams(window.location.search).get("mode");
  const surface = mode === "workspace" ? <UnkeyedWorkspaceHost /> : <ShippedContainer />;
  createRoot(container).render(
    <AuthProviderStub>
      <LocaleProviderStub>
        {surface}
      </LocaleProviderStub>
    </AuthProviderStub>,
  );
  window.__academicHarness = { setIdentity: setHarnessIdentity };
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", mount, { once: true });
} else {
  mount();
}
