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
 * - `panel` renders `AcademicSourcesPanel` with no container at all: the panel
 *   instance is never keyed by identity and no workspace-level fence is mounted,
 *   so the search lifecycle is protected only by the panel's own owner check.
 *   That is the difference between proving an outcome and proving the mechanism
 *   that produces it: `studio` and `workspace` both show a student the other's
 *   search results never arrive, but only `panel` fails if the fence in the
 *   component is removed.
 *
 * The module is read through a namespace import so this same harness builds and
 * runs on both sides of the fix. Before it there was no inner unit at all - one
 * container holding state that outlived the principal - so `workspace` mode mounts
 * that same shipped tree, and both modes then cover the identical surface. That is
 * the point of the reproduction: this spec fails on `ec623a4b` and passes on the
 * fixed head.
 */
import { createRoot } from "react-dom/client";
import { useState, type ReactElement } from "react";
import * as academicStudio from "@/components/academic/AcademicResearchStudio";
import * as academicPanel from "@/components/academic/AcademicSourcesPanel";
import { createAcademicLabels, createAcademicTranslator, type AcademicLabelers, type AcademicTranslate } from "@/components/academic/academicStrings";
import { academicSourceTypes, type AcademicSource, type AcademicSourceType, type CitationStyleId } from "@/components/academic/academicSourceLibrary";
import { useAuth } from "@/components/AuthProvider";
import { AuthProviderStub, setHarnessIdentity, type HarnessIdentity } from "./harnessAuth";
import { LocaleProviderStub, useLocale } from "./harnessLocale";

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

/** The props a host must supply to embed the Sources panel on its own. */
type PanelProps = {
  sources: readonly AcademicSource[];
  onLibraryChange: (next: AcademicSource[]) => void;
  citationStyle: CitationStyleId;
  allowedSourceTypes: readonly AcademicSourceType[];
  requireFullText: boolean;
  t: AcademicTranslate;
  labels: AcademicLabelers;
  workspaceId?: string | null;
};

type PanelModule = {
  AcademicSourcesPanel: (props: PanelProps) => ReactElement | null;
};

/** Scope the workspace state belongs to; a null id means no signed-in principal. */
type WorkspacePrincipal = { userId: string | null; workspaceId: string | null };

const studio = academicStudio as unknown as StudioModule;
const ShippedContainer = studio.AcademicResearchStudio;
const InnerWorkspace: NonNullable<StudioModule["AcademicWorkspace"]> =
  studio.AcademicWorkspace ?? (() => <ShippedContainer />);
const SourcesPanel = (academicPanel as unknown as PanelModule).AcademicSourcesPanel;

/** Host that changes `principal` without remounting the workspace instance. */
function UnkeyedWorkspaceHost() {
  const { user, workspace } = useAuth();
  // Deliberately unmemoized: a host is allowed to hand the workspace a fresh scope
  // object on every render, so ownership must be decided on the identity it carries
  // rather than on the reference. Memoizing here would hide an infinite-render
  // hazard ("too many re-renders") behind the test.
  const principal = { userId: user?.id ?? null, workspaceId: workspace?.id ?? null };
  return <InnerWorkspace principal={principal} />;
}

/**
 * Embeds the panel exactly the way its own props document ("Optional host override for tests and
 * embedding"): no identity-keyed subtree above it, no workspace fence, and no remount when the
 * principal moves. The host holds the library and hands over a fresh `workspaceId` per render,
 * unmemoized, so ownership has to be decided inside the component or the previous student's
 * answer lands in the next student's view.
 */
function DirectPanelHost() {
  const { workspace } = useAuth();
  const [library, setLibrary] = useState<AcademicSource[]>([]);
  const { locale } = useLocale();
  const ta = createAcademicTranslator(locale);
  const labels = createAcademicLabels(ta);
  return (
    <SourcesPanel
      sources={library}
      onLibraryChange={setLibrary}
      citationStyle="apa7"
      allowedSourceTypes={academicSourceTypes}
      requireFullText={false}
      t={ta}
      labels={labels}
      workspaceId={workspace?.id ?? null}
    />
  );
}

function mount(): void {
  const container = document.getElementById("root");
  if (!container) return;
  setHarnessIdentity(window.__academicInitialIdentity ?? null);
  const mode = new URLSearchParams(window.location.search).get("mode");
  const surface =
    mode === "workspace" ? <UnkeyedWorkspaceHost /> : mode === "panel" ? <DirectPanelHost /> : <ShippedContainer />;
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
