import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const wizardStyles = readFileSync(new URL("./ProductAdInputWizard.module.css", import.meta.url), "utf8");

// WCAG 1.4.3 AA minimum for normal (non-large) text.
const MINIMUM_TEXT_CONTRAST = 4.5;

const namedColors: Record<string, string> = { white: "#ffffff" };

function ruleBody(selector: string): string {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const rule = wizardStyles.match(new RegExp(`(?:^|})\\s*${escaped}\\s*\\{([^}]*)\\}`));
  if (!rule) throw new Error(`missing style rule: ${selector}`);
  return rule[1];
}

function declaredTextColor(selector: string): string {
  const value = ruleBody(selector).match(/(?<![\w-])color:\s*([^;]+);/)?.[1]?.trim().toLowerCase();
  if (!value) throw new Error(`no color declared in: ${selector}`);
  return namedColors[value] ?? value;
}

function contrastRatio(foreground: string, background: string): number {
  const channel = (pair: string) => {
    const value = parseInt(pair, 16) / 255;
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  };
  const luminance = (color: string) => {
    const [red, green, blue] = [1, 3, 5].map((start) => channel(color.slice(start, start + 2)));
    return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
  };
  const [lighter, darker] = [luminance(foreground), luminance(background)].sort((first, second) => second - first);
  return (lighter + 0.05) / (darker + 0.05);
}

// Every text rule paired with the most conservative surface it paints on.
const textOnSurface: ReadonlyArray<Readonly<{ label: string; selector: string; surface: string }>> = [
  { label: "shell body text", selector: ".shell", surface: "#f7f9fb" },
  { label: "section eyebrow", selector: ".eyebrow", surface: "#f7fbff" },
  { label: "wizard heading", selector: ".header h1", surface: "#ffffff" },
  { label: "intro paragraph", selector: ".intro", surface: "#ffffff" },
  { label: "field label", selector: ".labelText", surface: "#ffffff" },
  { label: "required mark", selector: ".requiredMark", surface: "#ffffff" },
  { label: "typed input value", selector: '.field input[type="text"], .field textarea, .field select', surface: "#fbfcfe" },
  { label: "input placeholder", selector: '.field input::placeholder, .field textarea::placeholder', surface: "#fbfcfe" },
  { label: "help and file name", selector: ".helpText, .fileName", surface: "#ffffff" },
  { label: "validation error", selector: ".errorText", surface: "#ffffff" },
  { label: "file input", selector: ".fileInput", surface: "#f9fbfd" },
  { label: "file chooser button", selector: ".fileInput::file-selector-button", surface: "#edf4ff" },
  { label: "submit button", selector: ".submitButton", surface: "#285fc2" },
  { label: "preview heading", selector: ".previewHeading h2", surface: "#f7fbff" },
  { label: "preview mark", selector: ".previewMark", surface: "#edf4ff" },
  { label: "empty preview prompt", selector: ".emptyPreview", surface: "#f7fbff" },
  { label: "preview meta chip", selector: ".previewMeta span", surface: "#edf4ff" },
  { label: "preview headline", selector: ".previewCard h3", surface: "#ffffff" },
  { label: "preview caption", selector: ".previewCard p", surface: "#ffffff" },
  { label: "preview asset name", selector: ".previewAsset", surface: "#ffffff" },
  { label: "local-only notice", selector: ".localNotice", surface: "#f0faf5" },
];

describe("ProductAdInputWizard accessibility styles", () => {
  it("keeps every wizard text color at or above the WCAG AA contrast minimum", () => {
    const belowMinimum = textOnSurface
      .map(({ label, selector, surface }) => ({
        label,
        ratio: contrastRatio(declaredTextColor(selector), surface),
      }))
      .filter(({ ratio }) => ratio < MINIMUM_TEXT_CONTRAST)
      .map(({ label, ratio }) => `${label} ${ratio.toFixed(2)}:1`);

    expect(belowMinimum, "text colors below the 4.5:1 minimum").toEqual([]);
  });

  it("keeps the placeholder legible on the input surface", () => {
    expect(contrastRatio(declaredTextColor('.field input::placeholder, .field textarea::placeholder'), "#fbfcfe")).toBeGreaterThanOrEqual(MINIMUM_TEXT_CONTRAST);
  });

  it("retires the muted greys that failed the AA contrast minimum", () => {
    for (const rejected of ["#8998a7", "#6a7a8b", "#748496"]) {
      expect(wizardStyles, `sub-threshold color ${rejected}`).not.toContain(rejected);
    }
  });
});
