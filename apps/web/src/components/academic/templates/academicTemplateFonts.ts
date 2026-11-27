import { getFontToken } from "./academicTemplateCatalog";
import type { AcademicDesignSpec, AcademicTemplateCatalog } from "./academicTemplateTypes";

/**
 * Resolves the catalog's font tokens into CSS font stacks. The stacks themselves come
 * from the generated snapshot (single source of truth with the API preview renderer),
 * and every value is sanitised before it is placed into an inline style — same rule
 * the server-side renderer applies: a font-stack quote can never break out of the
 * attribute it lands in.
 */

/** Neutralises double quotes so a stack can never terminate an HTML attribute. */
export function sanitizeFontStack(stack: string): string {
  return stack.replace(/"/g, "'");
}

function stackFor(catalog: AcademicTemplateCatalog, token: string, fallback: string): string {
  const entry = getFontToken(catalog, token);
  return sanitizeFontStack(entry?.cssStack ?? fallback);
}

export interface AcademicPreviewTypography {
  bodyStack: string;
  headingStack: string;
  captionStack: string;
  monoStack: string;
  bodySizePt: number;
  headingSizePt: (level: number) => number;
  lineSpacing: number;
  arabicScript: boolean;
}

export function typographyFor(catalog: AcademicTemplateCatalog, spec: AcademicDesignSpec): AcademicPreviewTypography {
  const bodyStack = stackFor(catalog, spec.bodyFontToken, "serif");
  const headingStack = stackFor(catalog, spec.headingFontToken, bodyStack);
  const captionStack = stackFor(catalog, spec.captionFontToken, bodyStack);
  const monoStack = stackFor(catalog, spec.monoFontToken, "monospace");
  const bodyEntry = getFontToken(catalog, spec.bodyFontToken);
  return {
    bodyStack,
    headingStack,
    captionStack,
    monoStack,
    bodySizePt: spec.bodySizePt,
    headingSizePt: (level: number) =>
      Math.round(spec.bodySizePt * Math.pow(spec.typeScaleRatio, Math.max(0, 4 - level)) * 100) / 100,
    lineSpacing: spec.lineSpacing,
    arabicScript: Boolean(bodyEntry?.arabicScript),
  };
}
