/**
 * Client-side sessionStorage draft-key and restore policy primitive (issue #688).
 *
 * Privacy motivation: Social Studio (and sibling studios) persist unsent drafts in
 * sessionStorage under keys scoped only to a workspace identifier. A workspace id is
 * NOT an authentication boundary: after an account switch in the same tab, a second
 * identity could restore the previous identity's private draft. This module owns the
 * isolated policy of scoping every draft entry by BOTH the authenticated user identity
 * and the workspace id, rejecting anonymous/invalid identity, and never migrating an
 * unattributed workspace-only legacy draft across identities.
 *
 * Design constraints:
 * - Pure client module, provider-free, SSR-safe, no dependency on AuthProvider,
 *   SocialStudioView or socialStudioState (their owners integrate it deliberately).
 * - Synthetic-data friendly: callers pass an explicit storage store, so tests never
 *   need a browser and nothing is ever logged or exposed from stored payloads.
 * - Fail closed: invalid identity rejects reads/writes; foreign or malformed owners
 *   are purged on read; legacy drafts without provable ownership are quarantined
 *   (removed without ever surfacing their payload to the current caller).
 */

export type SessionDraftStore = {
  readonly length: number;
  key(index: number): string | null;
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
};

export type DraftPrincipal = {
  userId: string | null | undefined;
  workspaceId: string | null | undefined;
};

export type ResolvedDraftPrincipal = {
  userId: string;
  workspaceId: string;
};

/** Identity sentinel values that must never be treated as an authenticated user. */
const anonymousIdentitySentinels = new Set([
  "anonymous",
  "anon",
  "guest",
  "signed-out",
  "unknown",
  "null",
  "undefined",
  "default",
]);

const maxIdentityLength = 512;
const draftKeyRoot = "taslim:draft:";
const envelopeVersion = 1;

function identitySegment(value: string) {
  // Percent-encode separators so ids containing ':', '/' or '%2F-like text' cannot
  // collide across (user, workspace) segment boundaries. Mirrors the repo's existing
  // locale-persistence convention (e.g. "taslim-locale:user:user%2Fa").
  return encodeURIComponent(value.trim());
}

function validIdentityToken(value: string | null | undefined): value is string {
  if (typeof value !== "string") return false;
  const trimmed = value.trim();
  if (!trimmed) return false;
  if (trimmed.length > maxIdentityLength) return false;
  // Control characters and surrogate halves cannot be safely encoded or enumerated.
  if ([/[\u0000-\u001f\u007f]/, /[\uD800-\uDBFF](?![\uDC00-\uDFFF])/, /(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/].some((pattern) => pattern.test(trimmed))) return false;
  if (anonymousIdentitySentinels.has(trimmed.toLowerCase())) return false;
  return true;
}

/**
 * Validates a candidate identity. Returns null (fail closed) for anonymous, missing,
 * malformed or sentinel identities — an authentication boundary must be proven, not
 * inferred from a workspace id.
 */
export function resolveDraftPrincipal(principal: DraftPrincipal | null | undefined): ResolvedDraftPrincipal | null {
  if (!principal) return null;
  if (!validIdentityToken(principal.userId) || !validIdentityToken(principal.workspaceId)) return null;
  return { userId: principal.userId.trim(), workspaceId: principal.workspaceId.trim() };
}

export function isDraftPrincipal(principal: DraftPrincipal | null | undefined): principal is ResolvedDraftPrincipal {
  return resolveDraftPrincipal(principal) !== null;
}

/**
 * Deterministic, identity-bound draft key. Returns null when the principal is not a
 * proven authenticated identity, so callers cannot fall through to a workspace-only
 * (identity-less) key by accident.
 */
export function sessionDraftStorageKey(namespace: string, principal: DraftPrincipal | null | undefined): string | null {
  const resolved = resolveDraftPrincipal(principal);
  const ns = typeof namespace === "string" ? namespace.trim() : "";
  if (!resolved || !ns || ns.length > maxIdentityLength) return null;
  return `${draftKeyRoot}${identitySegment(ns)}:${identitySegment(resolved.userId)}:${identitySegment(resolved.workspaceId)}`;
}

type DraftEnvelope = {
  v: number;
  u: string;
  w: string;
  savedAt: number;
  d: unknown;
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function parseEnvelope(raw: string | null): DraftEnvelope | null {
  if (raw === null) return null;
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!isRecord(parsed) || parsed.v !== envelopeVersion) return null;
  if (typeof parsed.savedAt !== "number" || !Number.isFinite(parsed.savedAt) || parsed.savedAt < 0) return null;
  if (!validIdentityToken(parsed.u as string) || !validIdentityToken(parsed.w as string)) return null;
  return { v: envelopeVersion, u: (parsed.u as string).trim(), w: (parsed.w as string).trim(), savedAt: parsed.savedAt, d: parsed.d };
}

function safeStorage(store: SessionDraftStore | null | undefined): SessionDraftStore | null {
  if (!store || typeof store.getItem !== "function" || typeof store.setItem !== "function" || typeof store.removeItem !== "function") return null;
  return store;
}

function readItem(store: SessionDraftStore, key: string): string | null {
  try {
    return store.getItem(key);
  } catch {
    // Storage may be unavailable (private mode, disabled cookies, sandbox errors).
    return null;
  }
}

function writeItem(store: SessionDraftStore, key: string, value: string): boolean {
  try {
    store.setItem(key, value);
    return true;
  } catch {
    return false;
  }
}

function removeItem(store: SessionDraftStore, key: string): boolean {
  try {
    store.removeItem(key);
    return true;
  } catch {
    return false;
  }
}

function draftKeySegments(store: SessionDraftStore): string[] {
  const keys: string[] = [];
  try {
    const length = store.length;
    for (let index = 0; index < length; index += 1) {
      const key = store.key(index);
      if (typeof key === "string" && key.startsWith(draftKeyRoot)) keys.push(key);
    }
  } catch {
    return keys;
  }
  return keys;
}

/**
 * Persists a draft under the identity+workspace scoped key with an owner-binding
 * envelope. Returns false (and writes nothing) for anonymous/invalid principals or
 * storage failures. Never logs or retains raw draft content on failure paths.
 */
export function writeSessionDraft<T>(store: SessionDraftStore | null | undefined, namespace: string, principal: DraftPrincipal | null | undefined, data: T): boolean {
  const resolvedStore = safeStorage(store);
  const key = sessionDraftStorageKey(namespace, principal);
  if (!resolvedStore || !key) return false;
  const resolved = resolveDraftPrincipal(principal) as ResolvedDraftPrincipal;
  let serialized: string;
  try {
    const envelope: DraftEnvelope = { v: envelopeVersion, u: resolved.userId, w: resolved.workspaceId, savedAt: Date.now(), d: data };
    serialized = JSON.stringify(envelope);
  } catch {
    // Non-serializable payloads (circular references, BigInt) fail closed.
    return false;
  }
  return writeItem(resolvedStore, key, serialized);
}

export type ReadSessionDraftOptions = {
  /** When the stored entry is owned by a different identity, remove it. Defaults to true (privacy hygiene). */
  purgeOnOwnerMismatch?: boolean;
};

/**
 * Restore policy: reads ONLY the exact key scoped to this identity AND workspace and
 * only returns the payload when the stored owner binding matches the caller. Never
 * falls back to a workspace-only key, never reads neighboring identities' entries.
 * `parse` lets the integrating owner apply its own schema normalization; malformed or
 * foreign-owner entries restore as null.
 */
export function readSessionDraft<T>(store: SessionDraftStore | null | undefined, namespace: string, principal: DraftPrincipal | null | undefined, parse?: (value: unknown) => T | null, options: ReadSessionDraftOptions = {}): T | null {
  const resolvedStore = safeStorage(store);
  const key = sessionDraftStorageKey(namespace, principal);
  const resolved = resolveDraftPrincipal(principal);
  if (!resolvedStore || !key || !resolved) return null;
  const envelope = parseEnvelope(readItem(resolvedStore, key));
  if (!envelope) return null;
  if (envelope.u !== resolved.userId || envelope.w !== resolved.workspaceId) {
    if (options.purgeOnOwnerMismatch !== false) removeItem(resolvedStore, key);
    return null;
  }
  if (typeof parse === "function") {
    try {
      return parse(envelope.d);
    } catch {
      return null;
    }
  }
  return envelope.d as T;
}

/** Removes exactly this identity+workspace entry. Invalid principals remove nothing. */
export function clearSessionDraft(store: SessionDraftStore | null | undefined, namespace: string, principal: DraftPrincipal | null | undefined): boolean {
  const resolvedStore = safeStorage(store);
  const key = sessionDraftStorageKey(namespace, principal);
  if (!resolvedStore || !key) return false;
  return removeItem(resolvedStore, key);
}

export type PurgeDraftsResult = {
  removedKeys: string[];
  /** True when the request was refused because the identity was anonymous/invalid. */
  refusedInvalidIdentity: boolean;
};

/**
 * Logout / account-switch scrub for the signing-out identity only:
 * - with `workspaceId`: removes that user's entry for that workspace (any namespace);
 * - without `workspaceId`: removes that user's entries across every workspace;
 * - `namespace` optionally narrows to one studio.
 * It never touches entries of other identities, and refuses (without wiping anything)
 * when the caller cannot prove an authenticated identity.
 */
export function purgeSessionDraftsForUser(store: SessionDraftStore | null | undefined, userId: string | null | undefined, options: { workspaceId?: string | null; namespace?: string | null } = {}): PurgeDraftsResult {
  const resolvedStore = safeStorage(store);
  if (!resolvedStore) return { removedKeys: [], refusedInvalidIdentity: true };
  if (!validIdentityToken(userId)) return { removedKeys: [], refusedInvalidIdentity: true };
  const encodedUser = identitySegment(userId);
  const encodedWorkspace = validIdentityToken(options.workspaceId ?? null) ? identitySegment((options.workspaceId as string).trim()) : null;
  const encodedNamespace = validIdentityToken(options.namespace ?? null) ? identitySegment((options.namespace as string).trim()) : null;
  const removedKeys: string[] = [];
  for (const key of draftKeySegments(resolvedStore)) {
    const rest = key.slice(draftKeyRoot.length);
    const firstSep = rest.indexOf(":");
    if (firstSep < 0) continue;
    const namespaceSegment = rest.slice(0, firstSep);
    const remainder = rest.slice(firstSep + 1);
    const secondSep = remainder.indexOf(":");
    const userSegment = secondSep < 0 ? remainder : remainder.slice(0, secondSep);
    const workspaceSegment = secondSep < 0 ? "" : remainder.slice(secondSep + 1);
    if (userSegment !== encodedUser) continue;
    if (encodedNamespace && namespaceSegment !== encodedNamespace) continue;
    if (encodedWorkspace && workspaceSegment !== encodedWorkspace) continue;
    if (removeItem(resolvedStore, key)) removedKeys.push(key);
  }
  return { removedKeys, refusedInvalidIdentity: false };
}

export type DraftEntryDescriptor = {
  key: string;
  namespace: string | null;
  ownerUserId: string | null;
  ownerWorkspaceId: string | null;
  attributed: boolean;
  bytes: number;
};

/**
 * Privacy-safe introspection for tests and logout flows: enumerates draft entries and
 * their owner attribution WITHOUT ever returning stored draft content.
 */
export function scanSessionDrafts(store: SessionDraftStore | null | undefined): DraftEntryDescriptor[] {
  const resolvedStore = safeStorage(store);
  if (!resolvedStore) return [];
  const descriptors: DraftEntryDescriptor[] = [];
  for (const key of draftKeySegments(resolvedStore)) {
    const raw = readItem(resolvedStore, key);
    const bytes = raw?.length ?? 0;
    const envelope = parseEnvelope(raw);
    const rest = key.slice(draftKeyRoot.length);
    const segments = rest.split(":");
    const namespace = segments.length > 0 ? safeDecode(segments[0]) : null;
    const ownerUserId = envelope ? envelope.u : segments.length > 1 ? safeDecode(segments[1]) : null;
    const ownerWorkspaceId = envelope ? envelope.w : segments.length > 2 ? safeDecode(segments[2]) : null;
    descriptors.push({ key, namespace, ownerUserId, ownerWorkspaceId, attributed: !!envelope, bytes });
  }
  return descriptors;
}

function safeDecode(value: string | undefined): string | null {
  if (!value) return null;
  try {
    return decodeURIComponent(value);
  } catch {
    return null;
  }
}

export type LegacyDraftDisposition = "promoted" | "superseded-removed" | "quarantined-removed" | "absent" | "storage-unavailable";

/**
 * Workspace-only legacy draft handling. The pre-#688 key format stored drafts with NO
 * owner attribution, so ownership cannot be proven for the current identity:
 * - unattributed payloads are quarantined: the legacy entry is removed and its
 *   content is NEVER returned or copied to any identity-scoped key. This guarantees a
 *   workspace-only legacy draft can never migrate across identities;
 * - a legacy value that already carries a valid owner envelope is promoted only when
 *   the envelope owner exactly matches the caller, or superseded (legacy removed, the
 *   newer scoped entry kept) when the scoped key already exists;
 * - any other owner, or an anonymous/invalid caller, results in quarantine.
 * The return value is a disposition only; draft content is never exposed.
 */
export function retireLegacyWorkspaceDraft(store: SessionDraftStore | null | undefined, legacyKey: string, namespace: string, principal: DraftPrincipal | null | undefined): LegacyDraftDisposition {
  const resolvedStore = safeStorage(store);
  if (!resolvedStore) return "storage-unavailable";
  const key = typeof legacyKey === "string" ? legacyKey : "";
  if (!key) return "absent";
  const raw = readItem(resolvedStore, key);
  if (raw === null) return "absent";
  const scopedKey = sessionDraftStorageKey(namespace, principal);
  const resolved = resolveDraftPrincipal(principal);
  const envelope = parseEnvelope(raw);
  if (envelope && resolved && envelope.u === resolved.userId && envelope.w === resolved.workspaceId) {
    if (scopedKey && readItem(resolvedStore, scopedKey) !== null) {
      removeItem(resolvedStore, key);
      return "superseded-removed";
    }
    if (scopedKey && writeItem(resolvedStore, scopedKey, raw)) {
      removeItem(resolvedStore, key);
      return "promoted";
    }
    return "storage-unavailable";
  }
  // Unattributed or foreign: quarantine. The payload is dropped, never surfaced.
  removeItem(resolvedStore, key);
  return "quarantined-removed";
}

/** Returns the live tab sessionStorage when available; null during SSR or storage denial. */
export function defaultSessionDraftStore(): SessionDraftStore | null {
  if (typeof window === "undefined") return null;
  try {
    const store = window.sessionStorage;
    return safeStorage(store);
  } catch {
    return null;
  }
}
