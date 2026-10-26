import { beforeEach, describe, expect, it, vi } from "vitest";

const harness = vi.hoisted(() => ({
  audio: {
    paused: true,
    play: vi.fn<() => Promise<void>>(),
    pause: vi.fn(),
    currentTime: 0,
  },
  setPlaying: vi.fn(),
}));

vi.mock("react", async () => {
  const actual = await vi.importActual<typeof import("react")>("react");
  return {
    ...actual,
    useEffect: () => undefined,
    useRef: () => ({ current: harness.audio }),
    useState: (initial: unknown) => [initial, harness.setPlaying] as const,
  };
});

vi.mock("./LocaleProvider", () => ({
  useLocale: () => ({ t: (key: string) => key }),
}));

import { VoiceAudioPlayer } from "./VoiceStudioView";

type RenderNode = { type?: string | ((...args: never[]) => unknown); props?: { children?: RenderNode | RenderNode[]; onClick?: () => unknown } } | null;

function findButton(node: RenderNode): { onClick: () => unknown } {
  if (!node) throw new Error("button not found");
  if (node.type === "button" && node.props?.onClick) return { onClick: node.props.onClick };
  const children = node.props?.children;
  for (const child of Array.isArray(children) ? children : [children]) {
    if (child) {
      try {
        return findButton(child);
      } catch {
        // Continue through the player tree until the playback control is found.
      }
    }
  }
  throw new Error("button not found");
}

describe("VoiceAudioPlayer playback safety", () => {
  beforeEach(() => {
    harness.audio.play.mockReset();
    harness.audio.pause.mockReset();
    harness.setPlaying.mockReset();
    harness.audio.paused = true;
  });

  it("converts a browser play rejection into a stopped player state", async () => {
    harness.audio.play.mockRejectedValueOnce(new Error("autoplay blocked"));
    const button = findButton(VoiceAudioPlayer({ src: "/private/audio", label: "Narration" }) as unknown as RenderNode);

    button.onClick();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(harness.audio.play).toHaveBeenCalledOnce();
    expect(harness.setPlaying).toHaveBeenCalledWith(false);
  });
});
