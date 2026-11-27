/**
 * Build-time public visibility gate shared by the Standard Research entry link
 * and the signed-in Academic pages. It defaults closed; API authorization remains
 * authoritative and this public flag is not a permission boundary.
 */
export function isAcademicResearchVisible(): boolean {
  return process.env.NEXT_PUBLIC_ACADEMIC_RESEARCH_ENABLED === "true";
}
