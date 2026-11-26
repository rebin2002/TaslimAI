"use client";

import { Download } from "lucide-react";
import type { MediaJobArtifactDownloadState } from "@/lib/mediaJobArtifactState";

type MediaJobArtifactDownloadProps = Readonly<{
  /** Resolved asset file URL; only rendered while the artifact is downloadable. */
  href: string;
  state: MediaJobArtifactDownloadState;
  className: string;
  /** Localized download label: visible for the action variant, part of the accessible name for the icon variant. */
  label: string;
  /** Localized reason shown instead of an enabled control. */
  unavailableLabel: string;
  /** Icon-only controls carry no visible text, so the asset name completes their accessible name. */
  assetName?: string;
  variant?: "action" | "icon";
  iconSize?: number;
}>;

/**
 * Download affordance for the artifact of a finished media job. It stays a plain
 * link while the artifact is known, or unverified because the studio's asset
 * list has not resolved it yet, and becomes a disabled button naming the reason
 * once the asset row reports its file as gone. A disabled `<button>` rather than
 * a stripped link keeps the affordance in the same tab position and states why
 * there is nothing to download, in the visible text and therefore in the
 * accessible name too, matching the asset library's treatment.
 */
export function MediaJobArtifactDownload({
  href,
  state,
  className,
  label,
  unavailableLabel,
  assetName,
  variant = "action",
  iconSize = 15,
}: MediaJobArtifactDownloadProps) {
  const icon = <Download size={iconSize} aria-hidden="true" />;

  if (state.downloadable) {
    return variant === "icon" ? (
      <a className={className} href={href} aria-label={`${label} ${assetName ?? ""}`.trim()}>{icon}</a>
    ) : (
      <a className={className} href={href}>{icon} {label}</a>
    );
  }

  if (variant === "icon") {
    const accessibleName = `${label} ${assetName ?? ""} · ${unavailableLabel}`.replace(/\s+/g, " ").trim();
    return (
      <button type="button" className={className} disabled title={unavailableLabel} aria-label={accessibleName}>
        {icon}
      </button>
    );
  }

  return (
    <button type="button" className={className} disabled title={unavailableLabel}>
      {icon} {label} · {unavailableLabel}
    </button>
  );
}
