/**
 * Pure, fail-closed adapter for the authenticated Movie source-metadata read
 * model (#814), preparing the provider-free Makeover composition (#819).
 * Neither this function nor its consumers perform a fetch, upload, charge,
 * or provider call. The route owner must bind the response to the active
 * principal/project/workspace at fetch completion.
 */
import {
  describeMovieVideoMakeoverSource,
  type MovieVideoMakeoverSourceContext,
  type MovieVideoMakeoverSourceEntry,
} from "@/lib/movieVideoMakeoverSourceCatalog";

export type MovieMakeoverServerSourceRow = Readonly<{
  clipId: string;
  movieProjectId: string;
  assetId: string | null;
  clipStatus: string;
  assetType: string | null;
  assetStatus: string | null;
  contentType: string | null;
  sizeBytes: number | null;
  durationSeconds: number | null;
  width: number | null;
  height: number | null;
  frameRate: number | null;
  frameRateVerified: boolean;
  audioState: string | null;
  audioStateVerified: boolean;
  ownershipVerified: boolean;
  ownerIsRequester: boolean;
  visibilityPrivate: boolean;
  retentionVerified: boolean;
  storageAvailable: boolean;
  attachPermitted: boolean;
  blockers: readonly string[];
  pendingFacts: readonly string[];
}>;

export type MovieMakeoverServerSourceResponse = Readonly<{
  module: string;
  movieProjectId: string;
  workspaceId: string;
  sources: readonly MovieMakeoverServerSourceRow[];
}>;

export function describeVerifiedMovieMakeoverServerSources(
  response: MovieMakeoverServerSourceResponse | null | undefined,
  expectedProjectId: string | null,
  context: MovieVideoMakeoverSourceContext,
): readonly MovieVideoMakeoverSourceEntry[] {
  const principalId = context.principalId?.trim();
  const workspaceId = context.workspaceId?.trim();
  // No stale, foreign-project or wrong-workspace catalogue is allowed to
  // reach the source panel. An absent response means loading, not empty access.
  if (!principalId || !workspaceId || !expectedProjectId?.trim()
    || !response || response.module !== "video-makeover-sources"
    || response.movieProjectId !== expectedProjectId
    || response.workspaceId !== workspaceId || !Array.isArray(response.sources)) {
    return [];
  }
  const seen = new Set<string>();
  const result: MovieVideoMakeoverSourceEntry[] = [];
  for (const row of response.sources) {
    if (!row || row.movieProjectId !== expectedProjectId || typeof row.clipId !== "string"
      || !row.clipId.trim() || seen.has(row.clipId)) continue;
    seen.add(row.clipId);
    // A boolean ownership relationship from the authenticated server is enough
    // to bind the source to the requester ONLY when the server also verified
    // the asset/file ownership link. Never infer a principal ID from a clip.
    const selfOwned = row.ownershipVerified === true && row.ownerIsRequester === true;
    const source = describeMovieVideoMakeoverSource({
      assetId: row.assetId,
      name: row.clipId, // The server carries no private clip title; do not invent one.
      assetType: row.assetType,
      mimeType: row.contentType,
      clipStatus: row.clipStatus,
      assetStatus: row.assetStatus?.toLowerCase() === "archived" ? "Archived" : row.assetStatus,
      workspaceId,
      projectId: expectedProjectId,
      ownerPrincipalId: selfOwned ? principalId : null,
      visibility: row.visibilityPrivate === true ? "private" : null,
      retentionPolicyVerified: row.retentionVerified === true,
      hasFile: row.storageAvailable === true,
      // The current API does not verify preview capability. Unknown must stay
      // unknown rather than being asserted true.
      canPreview: null,
      sizeBytes: row.sizeBytes,
      durationSeconds: row.durationSeconds,
      width: row.width,
      height: row.height,
      frameRate: row.frameRateVerified === true ? row.frameRate : null,
      audioPresent: row.audioStateVerified === true
        ? row.audioState === "present" ? true : row.audioState === "absent" ? false : null
        : null,
    }, context);

    // Client catalogue checks cannot override the server's actual attach
    // admission. For private source drafts we additionally require verified
    // self-ownership; collaborators may inspect authorized clip metadata but
    // may not silently attach another person's asset.
    if (!selfOwned || row.attachPermitted !== true) {
      result.push({
        ...source,
        selectable: false,
        blockers: [
          ...source.blockers,
          {
            code: selfOwned ? "source_asset_unavailable" : "source_cross_principal",
            message: selfOwned
              ? "The server has not authorized attaching this clip."
              : "This clip has no verified ownership binding to the current creator.",
          },
        ],
      });
    } else {
      result.push(source);
    }
  }
  return result;
}
