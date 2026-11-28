import type { Asset } from "./api";

/**
 * The only part of an asset row that decides whether its artifact can still be
 * served: the stored-file flag the download endpoint is built from.
 */
export type MediaJobArtifactRecord = Pick<Asset, "id" | "hasFile">;

export type MediaJobArtifactReason = "available" | "missing" | "unverified";

export type MediaJobArtifactDownloadState = Readonly<{
  downloadable: boolean;
  reason: MediaJobArtifactReason;
}>;

/**
 * A finished media job (voice.generate / music.generate) stores only the asset
 * identity in its result, so the artifact can still go away afterwards: the
 * stored file expires or is pruned and the asset row reports `hasFile: false`,
 * which is precisely the state the download route answers with
 * `404 ASSET_FILE_NOT_FOUND`. An enabled download control then dead-ends, so it
 * has to read as disabled instead.
 *
 * The studio's workspace asset list is the only client-side authority on that,
 * and it is a snapshot. An asset the list has not seen yet therefore stays
 * `unverified` and keeps the control enabled: a just-generated artifact is
 * never announced as gone before its library row has actually loaded.
 */
export function mediaJobArtifactDownloadState(
  artifact: MediaJobArtifactRecord | null | undefined,
): MediaJobArtifactDownloadState {
  if (!artifact) return { downloadable: true, reason: "unverified" };
  return artifact.hasFile
    ? { downloadable: true, reason: "available" }
    : { downloadable: false, reason: "missing" };
}

/** Resolves a job result's asset identity against the studio's loaded rows. */
export function findMediaJobArtifact(
  assets: readonly MediaJobArtifactRecord[],
  assetId: string | null | undefined,
): MediaJobArtifactRecord | null {
  const id = assetId?.trim();
  if (!id) return null;
  return assets.find((asset) => asset.id === id) ?? null;
}
