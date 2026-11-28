import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { MediaJobArtifactDownload } from "./MediaJobArtifactDownload";
import type { MediaJobArtifactDownloadState } from "@/lib/mediaJobArtifactState";

const href = "/api/assets/asset-1/download";
const available: MediaJobArtifactDownloadState = { downloadable: true, reason: "available" };
const missing: MediaJobArtifactDownloadState = { downloadable: false, reason: "missing" };
const unverified: MediaJobArtifactDownloadState = { downloadable: true, reason: "unverified" };

function actionMarkup(state: MediaJobArtifactDownloadState) {
  return renderToStaticMarkup(
    <MediaJobArtifactDownload
      href={href}
      state={state}
      className="voice-secondary-action"
      label="Download"
      unavailableLabel="No file"
    />,
  );
}

function iconMarkup(state: MediaJobArtifactDownloadState) {
  return renderToStaticMarkup(
    <MediaJobArtifactDownload
      href={href}
      state={state}
      className="voice-download-icon"
      label="Download"
      unavailableLabel="No file"
      assetName="welcome.mp3"
      variant="icon"
    />,
  );
}

describe("MediaJobArtifactDownload", () => {
  it("renders a plain link while the generated artifact is present", () => {
    const html = actionMarkup(available);
    expect(html).toContain(`<a class="voice-secondary-action" href="${href}">`);
    expect(html).toContain("Download</a>");
    expect(html).not.toContain("button");
  });

  it("keeps the link enabled while the asset list has not resolved the artifact yet", () => {
    expect(actionMarkup(unverified)).toBe(actionMarkup(available));
    expect(iconMarkup(unverified)).toBe(iconMarkup(available));
  });

  it("disables the studio action instead of linking to a missing or expired artifact", () => {
    const html = actionMarkup(missing);
    expect(html).not.toContain(href);
    expect(html).toContain('<button type="button" class="voice-secondary-action" disabled="" title="No file">');
    // The reason is in the visible text, so it is announced without being hidden from sighted users.
    expect(html).toContain("Download · No file</button>");
  });

  it("disables the library row icon and keeps its asset named", () => {
    const html = iconMarkup(missing);
    expect(html).not.toContain(href);
    expect(html).toContain('aria-label="Download welcome.mp3 · No file"');
    expect(html).toContain("<button");
    expect(html).toContain("disabled");
  });

  it("names the asset on the enabled library row icon", () => {
    const html = iconMarkup(available);
    expect(html).toContain(`<a class="voice-download-icon" href="${href}" aria-label="Download welcome.mp3">`);
  });

  it("hides the decorative glyph from assistive technology in every state", () => {
    for (const html of [actionMarkup(available), actionMarkup(missing), iconMarkup(available), iconMarkup(missing)]) {
      expect(html).toContain('aria-hidden="true"');
    }
  });
});
