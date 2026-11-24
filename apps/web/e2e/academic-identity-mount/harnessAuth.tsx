/**
 * Test-only `useAuth` stand-in for the mounted academic harness.
 *
 * The harness must change the principal underneath an already-mounted studio,
 * which is the exact condition the privacy fence protects. Nothing here talks to
 * the application's real `AuthProvider`; it supplies only the three fields the
 * academic surface reads, from an external store so a switch re-renders the tree
 * synchronously, the same way a session refresh does in the shipped app.
 */
import { useSyncExternalStore, type ReactNode } from "react";

export type HarnessIdentity = { userId: string; workspaceId: string } | null;

let currentIdentity: HarnessIdentity = null;
const listeners = new Set<() => void>();

export function setHarnessIdentity(next: HarnessIdentity): void {
  currentIdentity = next;
  for (const listener of [...listeners]) listener();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

function readIdentity(): HarnessIdentity {
  return currentIdentity;
}

type HarnessUser = { id: string };
type HarnessWorkspace = { id: string };
export type HarnessAuthValue = {
  user: HarnessUser | null;
  workspace: HarnessWorkspace | null;
  refresh: () => Promise<void>;
};

export function useAuth(): HarnessAuthValue {
  const identity = useSyncExternalStore(subscribe, readIdentity, () => null);
  return {
    user: identity ? { id: identity.userId } : null,
    workspace: identity ? { id: identity.workspaceId } : null,
    refresh: async () => {
      // The harness switches identity through setHarnessIdentity; a revalidation
      // request would have no server behind it.
    },
  };
}

export function AuthProviderStub({ children }: Readonly<{ children: ReactNode }>) {
  return <>{children}</>;
}
