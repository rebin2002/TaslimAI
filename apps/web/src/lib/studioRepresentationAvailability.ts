/**
 * Live per-representation downloadability for the Document and Presentation
 * Studio result views (#1049, the consumer half of the #1117 contract).
 *
 * The problem this guards against
 * ------------------------------
 * A completed generation job renders its studio download buttons from the
 * publication-time `ResultJson` snapshot baked once by
 * `GenerationJobExecution.AddPublishedAssetReference`. That snapshot carries
 * identity only (`id`, `type`, `fileName`, `contentType`) and is immutable by
 * design, so after a retention prune the snapshot still advertises a rendition
 * whose download route answers `409 ASSET_FILE_UNAVAILABLE` (issue #1049).
 *
 * The documented contract (#1117, API half — owned elsewhere, this module
 * never edits `GenerationContracts.cs` or any server file): a succeeded job
 * DTO *may* carry a companion field, kept next to the snapshot rather than
 * inside it:
 *
 *   representationDownloads: [
 *     { id, type, fileName, contentType, canDownload }  // per rendition
 *   ]
 *
 * The field is omitted entirely when the read model was built without live
 * availability data, so every payload shape that exists today keeps working.
 *
 * Consumer rules encoded here
 * ---------------------------
 * 1. **Backwards compatibility.** If the field is absent, not an array, or the
 *    job object is missing, the result is `null` — "no live facts delivered" —
 *    and callers must render exactly what the snapshot advertises, as they do
 *    today. Nothing may be withheld on a legacy payload.
 * 2. **Withdraw only an explicit `false`.** A rendition is hidden only when the
 *    delivered facts say `canDownload === false` for that exact representation
 *    id. An id with no entry, or an entry whose `canDownload` is not a boolean,
 *    is unknown, and unknown keeps today's advertised behaviour — the route
 *    remains the source of truth for those.
 * 3. **Per rendition, never per asset.** A job whose PDF was pruned still
 *    renders its ready DOCX; the snapshot itself is never mutated here.
 * 4. **No leaking URLs or metadata.** This module reads only `id` and
 *    `canDownload` from each entry and returns `id -> boolean` facts. The
 *    `type`/`fileName`/`contentType` halves of an entry are deliberately
 *    dropped, so withheld renditions cannot reintroduce metadata into the DOM
 *    through this path; rendering continues to draw from the snapshot the
 *    consumer already had.
 * 5. **Id comparison is normalised** (trim + lowercase) because GUIDs arrive
 *    in different casings across systems; entries are matched, never rewritten.
 */

/** Live availability facts keyed by normalised representation id. */
export type StudioRepresentationAvailabilityFacts = ReadonlyMap<string, boolean>;

/** Minimal shape a studio renderer needs an availability decision for. */
type IdentifiedRepresentation = { id: string };

function normaliseRepresentationId(value: unknown): string | null {
  return typeof value === "string" && value.trim() !== "" ? value.trim().toLowerCase() : null;
}

/**
 * Reads the companion `representationDownloads` facts from a job DTO.
 *
 * Returns `null` when the API delivered no live availability data (the field
 * is absent, `null`, or not an array), which every caller must treat as
 * "behave exactly as before". Returns a map of `id -> canDownload` for the
 * entries that carry a string id and a boolean promise; malformed entries are
 * ignored rather than guessed at.
 */
export function readStudioRepresentationDownloadability(
  job: unknown,
): StudioRepresentationAvailabilityFacts | null {
  if (!job || typeof job !== "object") return null;
  const field = (job as Record<string, unknown>)["representationDownloads"];
  if (!Array.isArray(field)) return null;
  const facts = new Map<string, boolean>();
  for (const entry of field) {
    if (!entry || typeof entry !== "object") continue;
    const record = entry as Record<string, unknown>;
    const id = normaliseRepresentationId(record["id"]);
    const canDownload = record["canDownload"];
    if (id === null || typeof canDownload !== "boolean") continue;
    facts.set(id, canDownload);
  }
  return facts;
}

/**
 * Whether one advertised rendition may still be offered a download button.
 *
 * With no delivered facts (`null`) everything stays downloadable, matching the
 * pre-#1049 client byte-for-byte. With facts, only an explicit `false` for
 * this exact id withdraws the promise.
 */
export function isStudioRepresentationDownloadable(
  representationId: string | null | undefined,
  facts: StudioRepresentationAvailabilityFacts | null,
): boolean {
  if (facts === null) return true;
  const id = normaliseRepresentationId(representationId);
  if (id === null) return true;
  return facts.get(id) !== false;
}

/**
 * The renditions a studio result view may render download controls for, in
 * snapshot order. Items are returned by reference — this never copies or
 * rehydrates metadata from the availability entries (see rule 4).
 */
export function filterStudioDownloadableRepresentations<T extends IdentifiedRepresentation>(
  representations: readonly T[] | undefined | null,
  facts: StudioRepresentationAvailabilityFacts | null,
): T[] {
  if (!Array.isArray(representations)) return [];
  if (facts === null) return [...representations];
  return representations.filter((representation) =>
    isStudioRepresentationDownloadable(representation.id, facts),
  );
}
