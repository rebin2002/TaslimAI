/**
 * Issue #1259 (Academic Research Studio) — route constants.
 *
 * Kept in its own module so the Phase A surface and its tests share one source
 * of truth without importing `navigation.ts`, which another lane owns.
 */

/** Existing standard Research Studio. */
export const academicStandardRoute = "/create/research";

/** This Phase A academic surface. */
export const academicRoute = "/create/research/academic";

/** Assets library: where finished standard research reports live. */
export const academicAssetsRoute = "/assets";

/** Merged academic layout-template gallery (issue #1265) — 230 template designs. */
export const academicTemplatesRoute = "/create/academic-templates";

/** Issue #1267 slice: export an existing saved report through a chosen template design. */
export const academicExportRoute = "/create/research/academic/export";
