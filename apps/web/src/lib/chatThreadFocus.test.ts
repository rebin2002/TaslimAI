import { describe, expect, it } from "vitest";
import { chatThreadAriaCurrent, resolveChatThreadListMount } from "@/lib/chatThreadFocus";

/**
 * Synthetic fixtures only: fabricated conversation ids and counts. No provider,
 * network, API client, or user conversation data is involved.
 */
const SYNTHETIC_ACTIVE = "00000000-0000-4000-8000-0000aaaa";
const SYNTHETIC_OTHER = "00000000-0000-4000-8000-0000bbbb";

describe("resolveChatThreadListMount", () => {
  it("keeps a rendered thread list mounted while another thread loads", () => {
    // The activated row owns keyboard focus. Swapping the populated list for the
    // first-load skeleton unmounts it and focus falls back to document.body.
    expect(resolveChatThreadListMount(3, true)).toEqual({ renderThreads: true, revalidating: true });
  });

  it("shows the skeleton only when nothing is rendered yet", () => {
    expect(resolveChatThreadListMount(0, true)).toEqual({ renderThreads: false, revalidating: false });
  });

  it("settles to a plain list once loading finishes", () => {
    expect(resolveChatThreadListMount(3, false)).toEqual({ renderThreads: true, revalidating: false });
    // An empty account that has finished loading renders the empty copy, not a
    // skeleton, so loading alone never gates the list.
    expect(resolveChatThreadListMount(0, false)).toEqual({ renderThreads: true, revalidating: false });
  });

  it("never reports a settled list as busy", () => {
    for (const count of [0, 1, 40]) {
      expect(resolveChatThreadListMount(count, false).revalidating).toBe(false);
    }
  });

  it("keeps the list mounted for a single rendered thread", () => {
    // One row is enough to lose focus from, so the guard is not a bulk-list case.
    expect(resolveChatThreadListMount(1, true).renderThreads).toBe(true);
  });
});

describe("chatThreadAriaCurrent", () => {
  it("marks only the thread that is on screen", () => {
    expect(chatThreadAriaCurrent(SYNTHETIC_ACTIVE, SYNTHETIC_ACTIVE)).toBe("true");
    expect(chatThreadAriaCurrent(SYNTHETIC_ACTIVE, SYNTHETIC_OTHER)).toBeUndefined();
  });

  it("asserts nothing while a draft owns the composer", () => {
    // A new chat selects no conversation, so no row may claim aria-current.
    expect(chatThreadAriaCurrent(null, SYNTHETIC_OTHER)).toBeUndefined();
    expect(chatThreadAriaCurrent(undefined, SYNTHETIC_OTHER)).toBeUndefined();
    expect(chatThreadAriaCurrent("", SYNTHETIC_OTHER)).toBeUndefined();
  });

  it("does not treat an empty row id as the selected thread", () => {
    expect(chatThreadAriaCurrent("", "")).toBeUndefined();
  });

  it("compares identities literally", () => {
    // Titles are localized and can collide; the id is the only stable key.
    expect(chatThreadAriaCurrent("thread-1", "Thread-1")).toBeUndefined();
    expect(chatThreadAriaCurrent(" thread-1", "thread-1")).toBeUndefined();
  });
});
