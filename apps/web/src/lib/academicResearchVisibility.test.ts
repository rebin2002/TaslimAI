import { afterEach, describe, expect, it, vi } from "vitest";
import { isAcademicResearchVisible } from "./academicResearchVisibility";

afterEach(() => vi.unstubAllEnvs());

describe("Academic Research visibility gate", () => {
  it("defaults off when unset and only opens for the exact true value", () => {
    vi.stubEnv("NEXT_PUBLIC_ACADEMIC_RESEARCH_ENABLED", undefined);
    expect(isAcademicResearchVisible()).toBe(false);

    vi.stubEnv("NEXT_PUBLIC_ACADEMIC_RESEARCH_ENABLED", "false");
    expect(isAcademicResearchVisible()).toBe(false);

    vi.stubEnv("NEXT_PUBLIC_ACADEMIC_RESEARCH_ENABLED", "TRUE");
    expect(isAcademicResearchVisible()).toBe(false);

    vi.stubEnv("NEXT_PUBLIC_ACADEMIC_RESEARCH_ENABLED", "true");
    expect(isAcademicResearchVisible()).toBe(true);
  });
});
