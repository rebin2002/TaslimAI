import { describe, expect, it, vi } from "vitest";
import {
  clearSessionDraft,
  defaultSessionDraftStore,
  purgeSessionDraftsForUser,
  readSessionDraft,
  resolveDraftPrincipal,
  retireLegacyWorkspaceDraft,
  scanSessionDrafts,
  sessionDraftStorageKey,
  writeSessionDraft,
  type SessionDraftStore,
} from "./sessionDraftScope";

/** In-memory synthetic store; no browser, no provider, no real user data. */
function createMemoryStore(): SessionDraftStore & { dump(): Record<string, string> } {
  const map = new Map<string, string>();
  return {
    get length() {
      return map.size;
    },
    key(index: number) {
      return Array.from(map.keys())[index] ?? null;
    },
    getItem(key: string) {
      return map.has(key) ? map.get(key)! : null;
    },
    setItem(key: string, value: string) {
      map.set(key, value);
    },
    removeItem(key: string) {
      map.delete(key);
    },
    dump() {
      return Object.fromEntries(map);
    },
  };
}

const accountA = { userId: "user-a", workspaceId: "workspace-1" };
const accountB = { userId: "user-b", workspaceId: "workspace-1" };
const namespace = "social-draft";
const privateDraft = { prompt: "synthetic private prompt", brandVoice: "synthetic voice" };

describe("sessionDraftScope key derivation", () => {
  it("is deterministic and binds BOTH identity and workspace", () => {
    const first = sessionDraftStorageKey(namespace, accountA);
    expect(first).toBe(sessionDraftStorageKey(namespace, accountA));
    expect(first).toContain("user-a");
    expect(first).toContain("workspace-1");
    expect(sessionDraftStorageKey(namespace, accountB)).not.toBe(first);
    expect(sessionDraftStorageKey(namespace, { userId: "user-a", workspaceId: "workspace-2" })).not.toBe(first);
    expect(sessionDraftStorageKey("other-ns", accountA)).not.toBe(first);
  });

  it("segment-encodes identifiers so ids cannot collide across boundaries", () => {
    const slash = sessionDraftStorageKey(namespace, { userId: "user/a", workspaceId: "b:c" });
    const encoded = sessionDraftStorageKey(namespace, { userId: "user%2Fa", workspaceId: "b%3Ac" });
    expect(slash).not.toBe(encoded);
    expect(slash).toContain("user%2Fa");
  });

  it("rejects anonymous or malformed identities with a null key", () => {
    const invalid: unknown[] = [
      null,
      undefined,
      { userId: null, workspaceId: "w" },
      { userId: "user-a", workspaceId: null },
      { userId: "   ", workspaceId: "w" },
      { userId: "anonymous", workspaceId: "w" },
      { userId: "Guest", workspaceId: "w" },
      { userId: "signed-out", workspaceId: "w" },
      { userId: 42, workspaceId: "w" },
      { userId: "user-a", workspaceId: "bad\u0000id" },
      { userId: "u".repeat(600), workspaceId: "w" },
    ];
    for (const principal of invalid) {
      expect(sessionDraftStorageKey(namespace, principal as never)).toBeNull();
    }
    expect(resolveDraftPrincipal(accountA)).toEqual({ userId: "user-a", workspaceId: "workspace-1" });
  });
});

describe("sessionDraftScope account-switch isolation", () => {
  it("Account B in the same tab and workspace cannot restore Account A's draft", () => {
    const store = createMemoryStore();
    expect(writeSessionDraft(store, namespace, accountA, privateDraft)).toBe(true);
    // A reload keeps A's draft restorable (same tab, same identity).
    expect(readSessionDraft(store, namespace, accountA)).toEqual(privateDraft);
    // Sign out A, sign in B against the SAME workspace id: must see nothing.
    expect(readSessionDraft(store, namespace, accountB)).toBeNull();
    // A's entry itself is untouched for A's next visit.
    expect(readSessionDraft(store, namespace, accountA)).toEqual(privateDraft);
  });

  it("writes and clears refuse anonymous identities and never touch stored entries", () => {
    const store = createMemoryStore();
    writeSessionDraft(store, namespace, accountA, privateDraft);
    const before = store.dump();
    expect(writeSessionDraft(store, namespace, { userId: "anonymous", workspaceId: "workspace-1" }, privateDraft)).toBe(false);
    expect(readSessionDraft(store, namespace, null)).toBeNull();
    expect(clearSessionDraft(store, namespace, { userId: "", workspaceId: "workspace-1" })).toBe(false);
    expect(store.dump()).toEqual(before);
  });

  it("drops entries whose stored owner no longer matches the scoped key holder", () => {
    const store = createMemoryStore();
    const key = sessionDraftStorageKey(namespace, accountB)!;
    // Synthetic tamper: key for B but envelope claims A.
    store.setItem(key, JSON.stringify({ v: 1, u: "user-a", w: "workspace-1", savedAt: 1, d: privateDraft }));
    expect(readSessionDraft(store, namespace, accountB)).toBeNull();
    expect(store.getItem(key)).toBeNull();
    // Retention mode for callers that only want policy answers.
    store.setItem(key, JSON.stringify({ v: 1, u: "user-a", w: "workspace-1", savedAt: 1, d: privateDraft }));
    expect(readSessionDraft(store, namespace, accountB, undefined, { purgeOnOwnerMismatch: false })).toBeNull();
    expect(store.getItem(key)).not.toBeNull();
  });

  it("applies the owner's parse hook and tolerates malformed envelopes", () => {
    const store = createMemoryStore();
    const key = sessionDraftStorageKey(namespace, accountA)!;
    store.setItem(key, "{not json");
    expect(readSessionDraft(store, namespace, accountA)).toBeNull();
    writeSessionDraft(store, namespace, accountA, { prompt: "synthetic", junk: 1 });
    expect(readSessionDraft(store, namespace, accountA, (value) => (value && typeof value === "object" && "prompt" in value ? { prompt: String((value as { prompt: unknown }).prompt) } : null))).toEqual({ prompt: "synthetic" });
  });
});

describe("sessionDraftScope legacy workspace-only drafts", () => {
  // Pre-#688 format: key scoped to workspace only, value = raw draft JSON, no owner.
  const legacyKeyFor = (workspaceId: string) => `taslim:social-draft:${workspaceId}`;

  it("quarantines an unattributed legacy draft instead of migrating it across identities", () => {
    const store = createMemoryStore();
    store.setItem(legacyKeyFor("workspace-1"), JSON.stringify(privateDraft));
    // Account B (the switching identity) must never obtain A's private legacy payload.
    expect(retireLegacyWorkspaceDraft(store, legacyKeyFor("workspace-1"), namespace, accountB)).toBe("quarantined-removed");
    expect(store.getItem(legacyKeyFor("workspace-1"))).toBeNull();
    expect(readSessionDraft(store, namespace, accountB)).toBeNull();
    // Nothing was copied into any scoped key for B.
    expect(sessionDraftStorageKey(namespace, accountB) ? store.getItem(sessionDraftStorageKey(namespace, accountB)!) : null).toBeNull();
  });

  it("quarantines legacy drafts even for the original workspace visitor without proof of ownership", () => {
    const store = createMemoryStore();
    store.setItem(legacyKeyFor("workspace-1"), JSON.stringify(privateDraft));
    expect(retireLegacyWorkspaceDraft(store, legacyKeyFor("workspace-1"), namespace, accountA)).toBe("quarantined-removed");
    expect(readSessionDraft(store, namespace, accountA)).toBeNull();
  });

  it("promotes an attributed legacy envelope only for its exact owner and refuses foreign owners", () => {
    const store = createMemoryStore();
    const attributed = JSON.stringify({ v: 1, u: "user-a", w: "workspace-1", savedAt: 5, d: privateDraft });
    store.setItem(legacyKeyFor("workspace-1"), attributed);
    expect(retireLegacyWorkspaceDraft(store, legacyKeyFor("workspace-1"), namespace, accountB)).toBe("quarantined-removed");

    store.setItem(legacyKeyFor("workspace-1"), attributed);
    expect(retireLegacyWorkspaceDraft(store, legacyKeyFor("workspace-1"), namespace, accountA)).toBe("promoted");
    expect(readSessionDraft(store, namespace, accountA)).toEqual(privateDraft);
    expect(store.getItem(legacyKeyFor("workspace-1"))).toBeNull();
    // Anonymous callers cannot trigger promotion either.
    store.setItem(legacyKeyFor("workspace-1"), attributed);
    expect(retireLegacyWorkspaceDraft(store, legacyKeyFor("workspace-1"), namespace, null)).toBe("quarantined-removed");
    // A newer scoped entry wins; legacy is superseded, not overwritten.
    writeSessionDraft(store, namespace, accountA, { prompt: "newer synthetic" });
    store.setItem(legacyKeyFor("workspace-1"), attributed);
    expect(retireLegacyWorkspaceDraft(store, legacyKeyFor("workspace-1"), namespace, accountA)).toBe("superseded-removed");
    expect(readSessionDraft(store, namespace, accountA)).toEqual({ prompt: "newer synthetic" });
  });

  it("reports absent legacy entries without side effects", () => {
    const store = createMemoryStore();
    expect(retireLegacyWorkspaceDraft(store, legacyKeyFor("workspace-9"), namespace, accountA)).toBe("absent");
    expect(retireLegacyWorkspaceDraft(store, "", namespace, accountA)).toBe("absent");
    expect(retireLegacyWorkspaceDraft(null, legacyKeyFor("workspace-1"), namespace, accountA)).toBe("storage-unavailable");
  });
});

describe("sessionDraftScope logout scrub", () => {
  it("removes only the signing-out user's entries across workspaces", () => {
    const store = createMemoryStore();
    writeSessionDraft(store, namespace, accountA, privateDraft);
    writeSessionDraft(store, namespace, { userId: "user-a", workspaceId: "workspace-2" }, { prompt: "synthetic two" });
    writeSessionDraft(store, "movie-draft", accountA, { prompt: "synthetic movie" });
    writeSessionDraft(store, namespace, accountB, { prompt: "private B" });

    const result = purgeSessionDraftsForUser(store, "user-a");
    expect(result.refusedInvalidIdentity).toBe(false);
    expect(result.removedKeys).toHaveLength(3);
    expect(readSessionDraft(store, namespace, accountA)).toBeNull();
    expect(readSessionDraft(store, "movie-draft", accountA)).toBeNull();
    // Another identity's draft is untouched by A's logout.
    expect(readSessionDraft(store, namespace, accountB)).toEqual({ prompt: "private B" });
  });

  it("narrows by workspace and namespace when the caller proves scope", () => {
    const store = createMemoryStore();
    writeSessionDraft(store, namespace, accountA, privateDraft);
    writeSessionDraft(store, namespace, { userId: "user-a", workspaceId: "workspace-2" }, privateDraft);
    const result = purgeSessionDraftsForUser(store, "user-a", { workspaceId: "workspace-1", namespace });
    expect(result.removedKeys).toEqual([sessionDraftStorageKey(namespace, accountA)]);
    expect(readSessionDraft(store, namespace, { userId: "user-a", workspaceId: "workspace-2" })).toEqual(privateDraft);
  });

  it("refuses to wipe anything for anonymous logout signals", () => {
    const store = createMemoryStore();
    writeSessionDraft(store, namespace, accountA, privateDraft);
    for (const userId of [null, undefined, "", "anonymous", "signed-out"]) {
      const result = purgeSessionDraftsForUser(store, userId as never);
      expect(result.removedKeys).toEqual([]);
      expect(result.refusedInvalidIdentity).toBe(true);
    }
    expect(readSessionDraft(store, namespace, accountA)).toEqual(privateDraft);
  });

  it("scan exposes attribution metadata without draft content", () => {
    const store = createMemoryStore();
    writeSessionDraft(store, namespace, accountA, privateDraft);
    const [descriptor] = scanSessionDrafts(store);
    expect(descriptor.namespace).toBe(namespace);
    expect(descriptor.ownerUserId).toBe("user-a");
    expect(descriptor.ownerWorkspaceId).toBe("workspace-1");
    expect(descriptor.attributed).toBe(true);
    const serialized = JSON.stringify(scanSessionDrafts(store));
    expect(serialized).not.toContain("synthetic private prompt");
  });
});

describe("sessionDraftScope storage resilience", () => {
  it("survives throwing storage without leaking or crashing", () => {
    const hostile: SessionDraftStore = {
      length: 0,
      key: () => null,
      getItem: () => {
        throw new Error("SecurityError");
      },
      setItem: () => {
        throw new Error("QuotaExceededError");
      },
      removeItem: () => {
        throw new Error("SecurityError");
      },
    };
    expect(writeSessionDraft(hostile, namespace, accountA, privateDraft)).toBe(false);
    expect(readSessionDraft(hostile, namespace, accountA)).toBeNull();
    expect(clearSessionDraft(hostile, namespace, accountA)).toBe(false);
    expect(purgeSessionDraftsForUser(hostile, "user-a").removedKeys).toEqual([]);
    expect(scanSessionDrafts(hostile)).toEqual([]);
    expect(retireLegacyWorkspaceDraft(hostile, "taslim:social-draft:workspace-1", namespace, accountA)).toBe("absent");
  });

  it("fails closed on non-serializable payloads and missing stores", () => {
    const circular: Record<string, unknown> = {};
    circular.self = circular;
    const store = createMemoryStore();
    expect(writeSessionDraft(store, namespace, accountA, circular)).toBe(false);
    expect(writeSessionDraft(null, namespace, accountA, privateDraft)).toBe(false);
    expect(writeSessionDraft(undefined, namespace, accountA, privateDraft)).toBe(false);
  });

  it("defaultSessionDraftStore is SSR-safe", () => {
    vi.stubGlobal("window", undefined);
    expect(defaultSessionDraftStore()).toBeNull();
    const store = createMemoryStore();
    vi.stubGlobal("window", { sessionStorage: store });
    expect(defaultSessionDraftStore()).toBe(store);
    vi.unstubAllGlobals();
  });
});
