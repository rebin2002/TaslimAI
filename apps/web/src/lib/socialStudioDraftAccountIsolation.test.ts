/**
 * Issue #688 — provider-free synthetic privacy QA for Social Studio unsent
 * draft isolation across a same-tab account switch (browser sessionStorage).
 *
 * SCOPE AND BOUNDARIES
 * - Synthetic fixtures only: no real accounts, workspaces, files, assets,
 *   prompts, provider calls, secrets, R2 access or production data.
 * - No browser, no dev server, no network, no provider, no charges. Pure
 *   state-module coverage against the public draft API, in the same harness
 *   style as the owner-claimed `socialStudioState.test.ts`.
 * - Read-only with respect to every claimed file: this file imports
 *   `socialStudioState` but never edits it, and touches neither
 *   `SocialStudioView.tsx`, `socialStudioState.test.ts` nor AuthProvider.
 * - Only the clearly synthetic sentinels below can ever appear in failure
 *   output, so no real user draft content can be exposed through CI logs.
 *
 * CONTRACT UNDER TEST
 * An unsent private draft persisted by synthetic Account A must not be
 * recoverable by independent synthetic Account B in the same browser tab,
 * even where a workspace identifier is reused across the two identities
 * (the worst-case collision assumed by the #688 audit) — and the same must
 * hold in the opposite direction. On current `main` the persisted draft
 * resolves from `taslim:social-draft:<workspaceId>` alone
 * (`socialStudioState.ts` `draftStoragePrefix` / `socialDraftStorageKey`),
 * and no draft-scoped cleanup runs at the sign-out boundary, so a workspace
 * identifier is functioning as the only authentication boundary.
 *
 * WHY EVERY EXPECTATION HERE IS BEHAVIORAL, NOT STORAGE-SHAPED
 * The issue permits two remedies: scope the persisted draft by authenticated
 * identity *and* workspace, or clear the stale private state at a correctly
 * identified login/logout/account-switch boundary. Asserting on raw key
 * names, or on whether a value survives anywhere in the namespace, would
 * certify one remedy over the other — an identity-scoped remedy legitimately
 * keeps Account A's draft in the tab, just unreachable by B. So each case
 * below asserts only what a later identity can obtain through the studio's
 * own read/write path, which both remedies satisfy.
 *
 * RED STATUS (intentional; no owned source file was touched)
 * The five isolation expectations are expected RED on `main` at 52a39468.
 * They document the required end state and must not be merged into release
 * CI before an owner-approved fix, because a test flipped to encode present
 * behavior would certify the leak. They go GREEN as written under the
 * identity-scoping remedy. Under a boundary-purge remedy the purge lives in
 * AuthProvider, a file this lane may not modify or reach, so these cases
 * must be ported to the auth-session lane (#279/#301) or driven through a
 * callable account-boundary export from the draft module. If the draft API
 * instead grows an identity parameter, the call sites below need the same
 * one-argument addition — the assertions themselves do not change.
 *
 * The four retention guards at the bottom encode behavior that is correct
 * today, must stay GREEN, and constrains how far any fix may reach.
 */
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  clearSocialDraft,
  persistSocialDraft,
  readSocialDraft,
  type SocialStudioDraft,
} from "./socialStudioState";

/**
 * Synthetic identities. The draft API exposes no identity parameter today,
 * which is precisely the gap: switching principals is invisible to the
 * persistence layer, so the same workspace address is handed to whoever next
 * resolves it in this tab. The ids are kept as fixtures so the isolation
 * cases cannot pass by accident if the sentinels ever converge.
 */
const ACCOUNT_A = "synthetic-account-a-688";
const ACCOUNT_B = "synthetic-account-b-688";
const SHARED_WORKSPACE_ID = "synthetic-workspace-688-shared";
const OTHER_WORKSPACE_ID = "synthetic-workspace-688-other";

/** Clearly synthetic private values; never real user content. */
const PRIVATE_A = {
  projectId: "synthetic-project-a-688",
  selectedFile: "synthetic-file-a-688",
  selectedAsset: "synthetic-asset-a-688",
  prompt: "SYNTHETIC-688-A private draft brief: Q3 launch narrative (never real data)",
  brandVoice: "SYNTHETIC-688-A private brand voice notes",
  audience: "SYNTHETIC-688-A private audience segment",
  callToAction: "SYNTHETIC-688-A private CTA",
} as const;

const PRIVATE_B = {
  projectId: "synthetic-project-b-688",
  selectedFile: "synthetic-file-b-688",
  selectedAsset: "synthetic-asset-b-688",
  prompt: "SYNTHETIC-688-B private draft brief: hiring announcement (never real data)",
} as const;

const draftA = (): SocialStudioDraft => ({
  projectId: PRIVATE_A.projectId,
  selectedFiles: [PRIVATE_A.selectedFile],
  selectedAssets: [PRIVATE_A.selectedAsset],
  prompt: PRIVATE_A.prompt,
  socialType: "announcement",
  platform: "linkedin",
  tone: "professional",
  language: "en",
  audience: PRIVATE_A.audience,
  brandVoice: PRIVATE_A.brandVoice,
  callToAction: PRIVATE_A.callToAction,
  includeHashtags: true,
  includeEmojis: false,
  generateVariants: false,
});

const draftB = (): SocialStudioDraft => ({
  ...draftA(),
  projectId: PRIVATE_B.projectId,
  selectedFiles: [PRIVATE_B.selectedFile],
  selectedAssets: [PRIVATE_B.selectedAsset],
  prompt: PRIVATE_B.prompt,
  audience: "",
  brandVoice: "",
  callToAction: "",
});

/** What the studio autosaves for an identity that arrives at an empty composer. */
const blankDraft = (): SocialStudioDraft => ({
  projectId: "",
  selectedFiles: [],
  selectedAssets: [],
  prompt: "",
  socialType: "auto",
  platform: "multi",
  tone: "professional",
  language: "auto",
  audience: "",
  brandVoice: "",
  callToAction: "",
  includeHashtags: true,
  includeEmojis: false,
  generateVariants: true,
});

/**
 * One browser tab == one sessionStorage. `vi.stubGlobal` replaces `window`
 * only, so the module's own `typeof window` guard and its storage-failure
 * paths stay in play. The store is intentionally minimal: identity is not an
 * input to the draft API, so an account transition is modeled by who reads
 * or writes next, exactly as the studio does after a re-render.
 */
const openTab = () => {
  const store = new Map<string, string>();
  vi.stubGlobal("window", {
    sessionStorage: {
      getItem: (key: string) => store.get(key) ?? null,
      setItem: (key: string, value: string) => {
        store.set(key, value);
      },
      removeItem: (key: string) => {
        store.delete(key);
      },
    },
  });
};

/** Account A composes an unsent private draft; the studio persists it. */
const composeAsA = () => {
  persistSocialDraft(SHARED_WORKSPACE_ID, draftA());
};

/** Account B composes and autosaves its own unsent private draft. */
const composeAsB = () => {
  persistSocialDraft(SHARED_WORKSPACE_ID, draftB());
};

const refsReachableByNextIdentity = (draft: SocialStudioDraft | null) => [
  ...new Set([...(draft?.selectedFiles ?? []), ...(draft?.selectedAssets ?? []), draft?.projectId ?? ""]),
];

describe("Social Studio unsent private draft isolation across a same-tab account switch (#688)", () => {
  afterEach(() => vi.unstubAllGlobals());

  // --- Isolation contract: expected RED on main until a fix lands. ---

  it("Account B cannot recover Account A's private prompt after signing in in the same tab", () => {
    openTab();
    composeAsA(); // A is signed in, draft persisted for the workspace.
    // Boundary: A signs out, B signs in and resolves the same workspace id.
    const visibleToB = readSocialDraft(SHARED_WORKSPACE_ID);

    expect(visibleToB?.prompt ?? "").not.toContain(PRIVATE_A.prompt);
    expect(visibleToB).toBeNull();
  });

  it("Account B cannot recover Account A's private selected file or asset identifiers", () => {
    openTab();
    composeAsA();
    const reachable = refsReachableByNextIdentity(readSocialDraft(SHARED_WORKSPACE_ID));

    expect(reachable).not.toContain(PRIVATE_A.selectedFile);
    expect(reachable).not.toContain(PRIVATE_A.selectedAsset);
    expect(reachable).not.toContain(PRIVATE_A.projectId);
  });

  it("Account B cannot recover Account A's private brand voice, audience or CTA copy", () => {
    openTab();
    composeAsA();
    const visibleToB = readSocialDraft(SHARED_WORKSPACE_ID);

    expect(visibleToB?.brandVoice ?? "").not.toContain(PRIVATE_A.brandVoice);
    expect(visibleToB?.audience ?? "").not.toContain(PRIVATE_A.audience);
    expect(visibleToB?.callToAction ?? "").not.toContain(PRIVATE_A.callToAction);
  });

  it("isolation survives Account B's autosave and a reload of Social Studio in the same tab", () => {
    openTab();
    composeAsA();
    // B's first mount must hydrate nothing; the studio then autosaves the
    // resulting empty composition, and a reload of the same tab must still not
    // surface Account A's private material on the second hydration. This
    // models the persisted-then-rewritten path, not just a bare second read.
    const firstMount = readSocialDraft(SHARED_WORKSPACE_ID);
    persistSocialDraft(SHARED_WORKSPACE_ID, firstMount ?? blankDraft());
    const afterReload = readSocialDraft(SHARED_WORKSPACE_ID);

    expect(firstMount).toBeNull();
    expect(afterReload?.prompt ?? "").not.toContain(PRIVATE_A.prompt);
    expect(afterReload?.brandVoice ?? "").not.toContain(PRIVATE_A.brandVoice);
    expect(afterReload?.selectedAssets ?? []).not.toContain(PRIVATE_A.selectedAsset);
  });

  it("Account A must not inherit Account B's private draft after B composes in the shared workspace", () => {
    openTab();
    composeAsA();
    composeAsB(); // B autosaves; today this overwrites the same workspace address.
    // A signs back in on the same tab and workspace and hydrates again.
    const visibleToA = readSocialDraft(SHARED_WORKSPACE_ID);

    // Not merely "no leak": A's private draft may not be silently destroyed by
    // the previous identity's save, so the studio must not hand A a foreign
    // draft in place of its own.
    expect(visibleToA?.prompt ?? "").not.toContain(PRIVATE_B.prompt);
    expect(visibleToA?.selectedAssets ?? []).not.toContain(PRIVATE_B.selectedAsset);
  });

  // --- Retention guards: correct today, and must stay green after the fix. ---

  it("guard: an account's own reload still restores its draft within one session", () => {
    openTab();
    composeAsA();

    const restored = readSocialDraft(SHARED_WORKSPACE_ID);
    expect(restored?.prompt).toBe(PRIVATE_A.prompt);
    expect(restored?.brandVoice).toBe(PRIVATE_A.brandVoice);
    expect(restored?.selectedFiles).toEqual([PRIVATE_A.selectedFile]);
    expect(restored?.selectedAssets).toEqual([PRIVATE_A.selectedAsset]);
    expect(restored?.projectId).toBe(PRIVATE_A.projectId);
  });

  it("guard: distinct workspace identifiers stay isolated within one account session", () => {
    openTab();
    composeAsA();

    expect(readSocialDraft(OTHER_WORKSPACE_ID)).toBeNull();
    expect(readSocialDraft(SHARED_WORKSPACE_ID)?.prompt).toBe(PRIVATE_A.prompt);
  });

  it("guard: discarding a draft removes only the addressed workspace entry", () => {
    openTab();
    composeAsA();
    persistSocialDraft(OTHER_WORKSPACE_ID, draftB());

    clearSocialDraft(OTHER_WORKSPACE_ID);

    expect(readSocialDraft(OTHER_WORKSPACE_ID)).toBeNull();
    // A scoped discard must not become collateral damage for the
    // still-signed-in account's own draft, which also bounds how far a future
    // boundary scrub may reach.
    expect(readSocialDraft(SHARED_WORKSPACE_ID)?.prompt).toBe(PRIVATE_A.prompt);
  });

  it("guard: synthetic fixtures stay non-overlapping between identities", () => {
    // Keeps the isolation cases meaningful: if A's and B's sentinels ever
    // converge, the assertions above would pass for the wrong reason.
    const aValues: string[] = Object.values(PRIVATE_A);
    const bValues: string[] = Object.values(PRIVATE_B);
    expect(aValues.some((value) => bValues.includes(value))).toBe(false);
    expect(ACCOUNT_A).not.toBe(ACCOUNT_B);
    expect(ACCOUNT_A).toContain("synthetic");
    expect(ACCOUNT_B).toContain("synthetic");
  });
});
