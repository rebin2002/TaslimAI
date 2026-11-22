/**
 * Issue #688 — Social Studio unsent private draft isolation across a SAME-TAB account switch.
 *
 * SYNTHETIC DATA ONLY. No browser binary, no providers, no network, no real accounts, no secrets
 * and no charged operations: one in-memory `sessionStorage` stand-in models the single storage area
 * a browser tab owns, which is exactly what makes the finding reachable — two accounts that use the
 * same tab share that area, while a workspace identifier can be identical for independent
 * identities.
 *
 * This file is the executable contract for the fix and the deliberate companion of the pinned RED
 * lane in PR #1054 (`socialStudioDraftAccountIsolation.test.ts`), which is not modified by this PR.
 * #1054 calls the draft API with a bare workspace id and signals no account boundary anywhere in
 * the file, so its five isolation cases and its retention guards are the same calls with opposite
 * expectations; this lane instead threads the authenticated principal through explicitly — the
 * "one-argument addition" #1054's own header anticipates — and asserts BOTH directions:
 *
 *   isolation   a later identity cannot obtain an earlier identity's private draft, and
 *   retention   the same identity still restores its own draft across a reload and navigation.
 *
 * No assertion here depends on a particular key spelling, so neither the identity-scoping remedy nor
 * a boundary-purge remedy is over-certified, and no assertion prints a stored payload.
 */
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  clearSocialDraft,
  legacySocialDraftStorageKey,
  persistSocialDraft,
  purgeSocialDraftsOnAccountBoundary,
  readSocialDraft,
  resolveSocialDraftScope,
  retireLegacySocialDraft,
  socialDraftScopeToken,
  socialDraftStorageKey,
  type SocialStudioDraft,
} from "./socialStudioState";

/** Synthetic identities that share one workspace identifier, exactly as the finding describes. */
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

const asDraft = (overrides: Partial<SocialStudioDraft> = {}): SocialStudioDraft => ({
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
  ...overrides,
});

const draftA = (): SocialStudioDraft => asDraft({
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
  generateVariants: false,
});

const draftB = (): SocialStudioDraft => asDraft({
  projectId: PRIVATE_B.projectId,
  selectedFiles: [PRIVATE_B.selectedFile],
  selectedAssets: [PRIVATE_B.selectedAsset],
  prompt: PRIVATE_B.prompt,
});

/**
 * One browser tab == one sessionStorage. `key`/`length` are provided because the account-boundary
 * scrub has to enumerate addresses; ordinary reads and writes still use exactly one address.
 * `reload()` re-creates the tab's JS globals over the SAME storage area, which is what a persisted
 * draft is meant to survive.
 */
type Tab = { store: Map<string, string>; reload: () => void; dump: () => string };
function openTab(): Tab {
  const store = new Map<string, string>();
  const reload = () => vi.stubGlobal("window", {
    sessionStorage: {
      getItem: (key: string) => store.get(key) ?? null,
      setItem: (key: string, value: string) => { store.set(key, value); },
      removeItem: (key: string) => { store.delete(key); },
      key: (index: number) => [...store.keys()][index] ?? null,
      get length() { return store.size; },
    },
  });
  reload();
  // `dump` exists only to prove private content is ABSENT from the tab.
  return { store, reload, dump: () => [...store.values()].join("\n") };
}

const aAtShared = () => ({ userId: ACCOUNT_A, workspaceId: SHARED_WORKSPACE_ID });
const bAtShared = () => ({ userId: ACCOUNT_B, workspaceId: SHARED_WORKSPACE_ID });
const aAtOther = () => ({ userId: ACCOUNT_A, workspaceId: OTHER_WORKSPACE_ID });

/** Everything a later identity could physically reach from a restored draft. */
const reachableRefs = (draft: SocialStudioDraft | null) => [
  ...new Set([...(draft?.selectedFiles ?? []), ...(draft?.selectedAssets ?? []), draft?.projectId ?? ""]),
];

afterEach(() => vi.unstubAllGlobals());

describe("Social Studio persisted draft: same-tab account switch (#688)", () => {
  it("gives two identities sharing a workspace id different persisted addresses", () => {
    openTab();
    const addressA = socialDraftStorageKey(aAtShared());
    const addressB = socialDraftStorageKey(bAtShared());
    expect(addressA).toBeTruthy();
    expect(addressB).toBeTruthy();
    expect(addressA).not.toBe(addressB);
    // Segment encoding must stop an id from smuggling a boundary across user/workspace halves.
    expect(socialDraftStorageKey({ userId: `${ACCOUNT_A}:${OTHER_WORKSPACE_ID}`, workspaceId: SHARED_WORKSPACE_ID }))
      .not.toBe(socialDraftStorageKey({ userId: ACCOUNT_A, workspaceId: `${OTHER_WORKSPACE_ID}:${SHARED_WORKSPACE_ID}` }));
  });

  it("Account B cannot recover Account A's private prompt after signing in in the same tab", () => {
    openTab();
    persistSocialDraft(aAtShared(), draftA());
    // Boundary: A signs out, B signs in and resolves the same workspace identifier.
    const visibleToB = readSocialDraft(bAtShared());
    expect(visibleToB).toBeNull();
    expect(visibleToB?.prompt ?? "").not.toContain(PRIVATE_A.prompt);
  });

  it("Account B cannot recover Account A's private selected file or asset identifiers", () => {
    openTab();
    persistSocialDraft(aAtShared(), draftA());
    const reachable = reachableRefs(readSocialDraft(bAtShared()));
    expect(reachable).not.toContain(PRIVATE_A.selectedFile);
    expect(reachable).not.toContain(PRIVATE_A.selectedAsset);
    expect(reachable).not.toContain(PRIVATE_A.projectId);
  });

  it("Account B cannot recover Account A's private brand voice, audience or CTA copy", () => {
    openTab();
    persistSocialDraft(aAtShared(), draftA());
    const visibleToB = readSocialDraft(bAtShared());
    expect(visibleToB?.brandVoice ?? "").not.toContain(PRIVATE_A.brandVoice);
    expect(visibleToB?.audience ?? "").not.toContain(PRIVATE_A.audience);
    expect(visibleToB?.callToAction ?? "").not.toContain(PRIVATE_A.callToAction);
  });

  it("isolation survives Account B's autosave and a reload of Social Studio in the same tab", () => {
    const tab = openTab();
    persistSocialDraft(aAtShared(), draftA());
    // B's first mount hydrates nothing, the studio autosaves the resulting empty composition, and a
    // reload of the SAME tab must still not surface A's private material on the second hydration.
    const firstMount = readSocialDraft(bAtShared());
    expect(firstMount).toBeNull();
    persistSocialDraft(bAtShared(), firstMount ?? asDraft());
    tab.reload();
    const afterReload = readSocialDraft(bAtShared());
    expect(afterReload?.prompt ?? "").not.toContain(PRIVATE_A.prompt);
    expect(afterReload?.brandVoice ?? "").not.toContain(PRIVATE_A.brandVoice);
    expect((afterReload?.selectedAssets ?? []).filter((id) => id.includes("-a-688"))).toEqual([]);
    // And the reload did not silently destroy A's own draft either.
    expect(readSocialDraft(aAtShared())?.prompt).toBe(PRIVATE_A.prompt);
    expect(tab.store.size).toBeGreaterThan(1);
  });

  it("Account A keeps its own private draft when Account B composes in the shared workspace", () => {
    openTab();
    persistSocialDraft(aAtShared(), draftA());
    persistSocialDraft(bAtShared(), draftB()); // B autosaves; must not overwrite A's address.
    const visibleToA = readSocialDraft(aAtShared());
    expect(visibleToA?.prompt).toBe(PRIVATE_A.prompt);
    expect(visibleToA?.selectedAssets).toEqual([PRIVATE_A.selectedAsset]);
    expect(visibleToA?.prompt ?? "").not.toContain(PRIVATE_B.prompt);
    expect(reachableRefs(visibleToA)).not.toContain(PRIVATE_B.selectedAsset);
    // B's copy is intact under B's address, and B still cannot see A's.
    expect(readSocialDraft(bAtShared())?.prompt).toBe(PRIVATE_B.prompt);
    expect(readSocialDraft(bAtShared())?.prompt ?? "").not.toContain(PRIVATE_A.prompt);
  });

  it("a tampered owner binding is refused and purged rather than trusted", () => {
    const tab = openTab();
    persistSocialDraft(aAtShared(), draftA());
    const addressA = socialDraftStorageKey(aAtShared()) as string;
    const stored = JSON.parse(tab.store.get(addressA) as string) as { d: unknown };
    // An attacker who can write sessionStorage cannot re-attribute it by editing the key alone.
    tab.store.set(socialDraftStorageKey(bAtShared()) as string, JSON.stringify({ v: 1, u: ACCOUNT_A, w: SHARED_WORKSPACE_ID, savedAt: 1, d: stored.d }));
    expect(readSocialDraft(bAtShared())).toBeNull();
    // #800's policy purges the foreign-owner entry on read, so it cannot linger in the tab.
    expect(tab.store.has(socialDraftStorageKey(bAtShared()) as string)).toBe(false);
    expect(readSocialDraft(aAtShared())?.prompt).toBe(PRIVATE_A.prompt);
  });

  it("the account boundary scrubs the departing identity and leaves the next one intact", () => {
    const tab = openTab();
    persistSocialDraft(aAtShared(), draftA());
    persistSocialDraft(bAtShared(), draftB());
    // A signs out while B is already resolved: exactly A's entries go, and only when A is proven.
    expect(purgeSocialDraftsOnAccountBoundary(ACCOUNT_A)).toEqual({ removedCount: 1, refusedInvalidIdentity: false });
    expect(readSocialDraft(aAtShared())).toBeNull();
    expect(readSocialDraft(bAtShared())?.prompt).toBe(PRIVATE_B.prompt);
    expect(tab.dump()).not.toContain(PRIVATE_A.prompt);
    expect(tab.dump()).not.toContain(PRIVATE_A.selectedAsset);
    // Refusing on an unproven identity is what keeps a half-resolved session from wiping data.
    expect(purgeSocialDraftsOnAccountBoundary(null).refusedInvalidIdentity).toBe(true);
    expect(purgeSocialDraftsOnAccountBoundary("signed-out").refusedInvalidIdentity).toBe(true);
    expect(readSocialDraft(bAtShared())?.prompt).toBe(PRIVATE_B.prompt);
  });

  it("a pre-#688 workspace-only draft is quarantined, never handed to whoever arrives first", () => {
    const tab = openTab();
    // Left behind by an older build: no owner attribution, so ownership cannot be proven.
    tab.store.set(legacySocialDraftStorageKey(SHARED_WORKSPACE_ID), JSON.stringify(draftA()));
    expect(readSocialDraft(bAtShared())).toBeNull();
    expect(retireLegacySocialDraft(bAtShared())).toBe("quarantined-removed");
    expect(tab.store.has(legacySocialDraftStorageKey(SHARED_WORKSPACE_ID))).toBe(false);
    expect(tab.dump()).not.toContain(PRIVATE_A.prompt);
    expect(tab.dump()).not.toContain(PRIVATE_A.selectedFile);
    // A's own later visit finds nothing either: the entry was removed, not migrated to a stranger.
    expect(readSocialDraft(aAtShared())).toBeNull();
  });

  it("preserves within-account navigation: the same identity restores its own draft", () => {
    openTab();
    persistSocialDraft(aAtShared(), draftA());
    // Navigate to a second workspace and back without ever leaving the account.
    expect(readSocialDraft(aAtOther())).toBeNull();
    persistSocialDraft(aAtOther(), draftB());
    expect(readSocialDraft(aAtShared())?.prompt).toBe(PRIVATE_A.prompt);
    expect(readSocialDraft(aAtOther())?.prompt).toBe(PRIVATE_B.prompt);
    // The hydration token is stable for one scope and distinct for another identity or workspace,
    // so a workspace switch inside an account never reads as "new session, hydrate nothing".
    expect(socialDraftScopeToken(aAtShared())).toBe(socialDraftScopeToken({ userId: ` ${ACCOUNT_A} `, workspaceId: SHARED_WORKSPACE_ID }));
    expect(socialDraftScopeToken(aAtShared())).not.toBe(socialDraftScopeToken(bAtShared()));
    expect(socialDraftScopeToken(aAtShared())).not.toBe(socialDraftScopeToken(aAtOther()));
    expect(resolveSocialDraftScope(aAtShared())).toEqual({ userId: ACCOUNT_A, workspaceId: SHARED_WORKSPACE_ID });
  });

  it("an unauthenticated or half-resolved session can neither read nor write a draft", () => {
    const tab = openTab();
    persistSocialDraft(aAtShared(), draftA());
    for (const anonymous of [null, undefined, "", "  ", "anonymous", "guest", "signed-out", "unknown"]) {
      const target = { userId: anonymous, workspaceId: SHARED_WORKSPACE_ID };
      expect(resolveSocialDraftScope(target)).toBeNull();
      expect(readSocialDraft(target)).toBeNull();
      expect(persistSocialDraft(target, draftB())).toBe(false);
      expect(clearSocialDraft(target)).toBe(false);
    }
    // The refusals are inert: they cannot disturb the signed-in identity's own entry either.
    expect(readSocialDraft(aAtShared())?.prompt).toBe(PRIVATE_A.prompt);
    expect(tab.dump()).not.toContain(PRIVATE_B.prompt);
  });

  it("degrades safely when storage is unavailable instead of leaking or throwing", () => {
    vi.stubGlobal("window", {
      get sessionStorage() { throw new Error("storage disabled by the browser"); },
    });
    expect(readSocialDraft(aAtShared())).toBeNull();
    expect(persistSocialDraft(aAtShared(), draftA())).toBe(false);
    expect(clearSocialDraft(aAtShared())).toBe(false);
    expect(retireLegacySocialDraft(aAtShared())).toBe("storage-unavailable");
    expect(purgeSocialDraftsOnAccountBoundary(ACCOUNT_A)).toEqual({ removedCount: 0, refusedInvalidIdentity: true });
    vi.unstubAllGlobals();
    // SSR: no `window` at all. Key derivation stays pure, storage stays unreachable.
    expect(readSocialDraft(aAtShared())).toBeNull();
    expect(persistSocialDraft(aAtShared(), draftA())).toBe(false);
    expect(socialDraftStorageKey(aAtShared())).toBeTruthy();
  });

  it("keeps the synthetic fixtures non-overlapping so isolation cannot pass by accident", () => {
    const aValues: string[] = Object.values(PRIVATE_A);
    const bValues: string[] = Object.values(PRIVATE_B);
    expect(aValues.some((value) => bValues.includes(value))).toBe(false);
    expect(ACCOUNT_A).not.toBe(ACCOUNT_B);
    expect(ACCOUNT_A).toContain("synthetic");
    expect(ACCOUNT_B).toContain("synthetic");
  });
});
