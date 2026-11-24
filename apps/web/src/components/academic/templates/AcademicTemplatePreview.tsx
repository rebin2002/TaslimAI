import type { CSSProperties } from "react";
import { syntheticBannerFor } from "./academicTemplateCatalog";
import { typographyFor } from "./academicTemplateFonts";
import { previewSampleFor, type AcademicPreviewSample } from "./academicTemplateSample";
import type { AcademicDesignSpec, AcademicTemplateCatalog } from "./academicTemplateTypes";

/**
 * Deterministic, purely client-side layout preview for one template configuration.
 * It renders the same structural elements as the server-side DOCX/PDF/HTML outputs
 * (cover, disclosure slot, contents, headings, table, quote, references with hanging
 * indent, footnote, folio with roman/arabic/none numbering and arabic-indic digits)
 * from the design spec alone — no network, no research content, and React escapes
 * every text node so injected markup can only ever appear as literal text.
 */

const ROMAN: [number, string][] = [
  [1000, "m"], [900, "cm"], [500, "d"], [400, "cd"], [100, "c"], [90, "xc"],
  [50, "l"], [40, "xl"], [10, "x"], [9, "ix"], [5, "v"], [4, "iv"], [1, "i"],
];

export function toRomanLower(number: number): string {
  let remaining = Math.max(0, Math.floor(number));
  let out = "";
  for (const [value, symbol] of ROMAN) {
    while (remaining >= value) {
      out += symbol;
      remaining -= value;
    }
  }
  return out;
}

export function toArabicIndic(digits: string): string {
  return [...digits]
    .map((character) => ("0" <= character && character <= "9" ? String.fromCharCode(0x0660 + Number(character)) : character))
    .join("");
}

/** Mirrors `AcademicRtlPolicy.PageLabel` including the arabic-indic digit system. */
export function pageLabel(number: number, scheme: string, spec: Pick<AcademicDesignSpec, "digitSystem">): string {
  if (scheme === "none") return "";
  let label = "";
  if (scheme === "roman-lower") label = toRomanLower(number);
  else if (scheme === "roman-upper") label = toRomanLower(number).toUpperCase();
  else label = String(number);
  if (spec.digitSystem === "arabic-indic") label = toArabicIndic(label);
  return label;
}

function stripNumbering(heading: string): string {
  return heading.replace(/^[0-9٠-٩۰-۹]+(\.[0-9٠-٩۰-۹]+)*\.?\s+/, "");
}

const folioAlign: Record<string, CSSProperties> = {
  "bottom-center": { textAlign: "center" },
  "bottom-inside": { textAlign: "start" },
  "bottom-outside": { textAlign: "end" },
  "top-center": { textAlign: "center" },
  "top-right": { textAlign: "end" },
};

export interface AcademicTemplatePreviewProps {
  catalog: AcademicTemplateCatalog;
  spec: AcademicDesignSpec;
  configurationId?: string;
  /** Optional override for tests/reviews; defaults to the language-appropriate synthetic sample. */
  sample?: Partial<AcademicPreviewSample>;
  showBanner?: boolean;
  className?: string;
}

export function AcademicTemplatePreview({
  catalog,
  spec,
  configurationId,
  sample,
  showBanner = true,
  className,
}: AcademicTemplatePreviewProps) {
  const base = previewSampleFor(spec.languageCode);
  const content: AcademicPreviewSample = { ...base, ...sample };
  const typography = typographyFor(catalog, spec);
  const scale = 0.45; // gallery-card mini-page scale; keeps physical pt settings proportional
  const pt = (value: number) => `${Math.round(value * scale * 100) / 100}px`;

  const rootStyle: CSSProperties = {
    direction: spec.direction,
    backgroundColor: spec.backgroundColorHex,
    color: spec.textColorHex,
    fontFamily: typography.bodyStack,
    fontSize: pt(spec.bodySizePt),
    lineHeight: spec.lineSpacing,
    padding: `${pt(spec.marginTopPt)} ${pt(spec.marginRightPt)} ${pt(spec.marginBottomPt)} ${pt(spec.marginLeftPt)}`,
    textAlign: spec.justifyBody ? "justify" : "start",
    columnCount: spec.columnCount > 1 ? spec.columnCount : undefined,
    columnGap: spec.columnCount > 1 ? pt(spec.columnGapPt) : undefined,
  };

  const headingStyle = (level: number): CSSProperties => ({
    fontFamily: typography.headingStack,
    fontSize: pt(typography.headingSizePt(level)),
    fontWeight: spec.headingsBold ? 700 : 400,
    textTransform: level === 1 && spec.h1Uppercase ? "uppercase" : undefined,
    textAlign: level === 1 && spec.coverStyle === "minimal-centered" ? "center" : undefined,
  });

  const paragraphStyle: CSSProperties = {
    marginTop: 0,
    marginBottom: pt(spec.paragraphAfterPt),
    textIndent: spec.firstLineIndentPt > 0 ? pt(spec.firstLineIndentPt) : undefined,
  };

  const inTextMarker =
    spec.citationMarkers === "numeric" ? "[1]" : spec.citationMarkers === "notes" ? "\u00b9" : "(Sample, Year)";

  const visibleToc = content.tocEntries.filter(
    (entry) => entry.label === "" || entry.label.split(".").length <= Math.max(1, spec.tocDepth),
  );

  const bodyFolio = pageLabel(3, spec.bodyNumbering, spec);
  const frontFolio = spec.frontMatterNumbering === "none" ? "" : pageLabel(4, spec.frontMatterNumbering, spec);

  return (
    <div
      className={`academic-preview ${className ?? ""}`.trim()}
      data-configuration-id={configurationId}
      dir={spec.direction}
      lang={spec.languageCode}
      style={rootStyle}
      aria-label={`Synthetic layout preview (${spec.languageCode}, ${spec.direction})`}
    >
      {showBanner ? (
        <div className="academic-preview-banner" style={{ color: spec.accentColorHex, marginBottom: pt(8), fontSize: pt(9) }}>
          {syntheticBannerFor(catalog, spec.languageCode)}
        </div>
      ) : null}

      <header className={`academic-cover academic-cover-${spec.coverStyle}`}>
        {spec.coverStyle === "none" ? null : (
          <>
            {spec.coverStyle === "institutional-band" ? (
              <div className="academic-cover-band" style={{ backgroundColor: spec.accentColorHex, height: pt(6), marginBottom: pt(8) }} />
            ) : null}
            <h1 className="academic-cover-title" style={headingStyle(1)}>{content.title}</h1>
            <p className="academic-cover-subtitle" style={{ ...paragraphStyle, fontStyle: "italic" }}>{content.subtitle}</p>
            {spec.coverStyle === "two-part" ? <hr style={{ borderColor: spec.accentColorHex }} /> : null}
            <p className="academic-cover-author" style={paragraphStyle}>{content.authorLine}</p>
            <p className="academic-cover-institution" style={paragraphStyle}>{content.institutionLine}</p>
            <p className="academic-cover-date" style={paragraphStyle}>{content.dateLine}</p>
          </>
        )}
        {frontFolio ? <div className="academic-folio academic-folio-frontmatter" style={folioAlign[spec.pageNumberPosition]}>{frontFolio}</div> : null}
      </header>

      {spec.runningHeadContent !== "none" ? (
        <div className="academic-running-head" style={{ fontSize: pt(9), textAlign: "center", fontFamily: typography.captionStack }}>
          {spec.runningHeadContent === "author" ? content.authorLine : content.title}
        </div>
      ) : null}

      {spec.includesApprovalPage ? (
        <div className="academic-approval-page" style={{ marginBottom: pt(spec.paragraphAfterPt) }}>
          <p style={paragraphStyle}>{content.institutionLine}</p>
        </div>
      ) : null}

      {spec.supportsRtlAbstractPage ? (
        <div className="academic-abstract-page academic-abstract-secondary" style={{ marginBottom: pt(spec.paragraphAfterPt) }}>
          <h2 style={headingStyle(2)} lang="en" dir="ltr">English Abstract (separate page slot)</h2>
          <p style={{ ...paragraphStyle, direction: "ltr", textAlign: "left" }}>{base.abstractText}</p>
        </div>
      ) : null}

      {spec.includesAiUseDisclosureSlot ? (
        <div className="academic-ai-disclosure" style={{ marginBottom: pt(spec.paragraphAfterPt), borderInlineStart: `2px solid ${spec.accentColorHex}`, paddingInlineStart: pt(6) }}>
          <h2 style={headingStyle(2)}>{content.aiDisclosureHeading}</h2>
          <p style={paragraphStyle}>{content.aiDisclosureText}</p>
        </div>
      ) : null}

      <h2 className="academic-abstract-heading" style={headingStyle(2)}>{content.abstractHeading}</h2>
      <p className="academic-abstract" style={paragraphStyle}>{content.abstractText}</p>

      {spec.includeToc ? (
        <nav className="academic-toc" aria-label={content.contentsHeading}>
          <h2 style={headingStyle(2)}>{content.contentsHeading}</h2>
          <ul style={{ listStyle: "none", margin: 0, padding: 0 }}>
            {visibleToc.map((entry) => (
              <li key={`${entry.label}-${entry.text}`} className="academic-toc-entry" style={{ display: "flex", alignItems: "baseline", gap: pt(4) }}>
                {entry.label ? <span className="academic-toc-number">{entry.label}</span> : null}
                <span className="academic-toc-title">{entry.text}</span>
                {spec.tocDotLeaders ? <span className="academic-toc-leader" style={{ flex: 1, borderBottom: `1px dotted ${spec.textColorHex}`, minWidth: pt(10) }} /> : null}
              </li>
            ))}
          </ul>
        </nav>
      ) : null}

      <main className="academic-body" style={{ breakInside: "avoid" }}>
        {content.sections.map((section, index) => (
          <section key={section.heading}>
            <h2 className={`academic-heading academic-h${index === 0 ? 1 : 2}`} style={headingStyle(index === 0 ? 1 : 2)}>
              {spec.headingNumbering === "none" ? stripNumbering(section.heading) : section.heading}
            </h2>
            <p style={paragraphStyle}>
              {section.body} <span className="academic-citation-marker">{inTextMarker}</span>
            </p>
          </section>
        ))}

        <figure className="academic-table-figure" style={{ margin: 0, marginBottom: pt(spec.paragraphAfterPt) }}>
          <figcaption className="academic-caption academic-table-caption" style={{ fontFamily: typography.captionStack, fontSize: pt(9), captionSide: "top" as never }}>
            {content.tableCaption}
          </figcaption>
          <table style={{ borderCollapse: "collapse", width: "100%", fontSize: pt(9) }}>
            <tbody>
              {content.tableRows.map((row, rowIndex) => (
                <tr key={rowIndex}>
                  {row.map((cell, cellIndex) => (
                    <td
                      key={cellIndex}
                      style={{
                        border: `1px solid ${spec.textColorHex}`,
                        padding: `${pt(2)} ${pt(4)}`,
                        fontWeight: rowIndex === 0 ? 700 : 400,
                        backgroundColor: rowIndex === 0 ? spec.accentColorHex : undefined,
                      }}
                    >
                      {cell}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </figure>

        <blockquote
          className="academic-quote"
          style={{ margin: 0, marginInlineStart: pt(spec.firstLineIndentPt > 0 ? spec.firstLineIndentPt * 2 : 18), borderInlineStart: `2px solid ${spec.accentColorHex}`, paddingInlineStart: pt(6), fontStyle: "italic" }}
        >
          <p style={paragraphStyle}>{content.quoteText}</p>
          <footer style={{ fontSize: pt(9) }}>{content.quoteAttribution}</footer>
        </blockquote>

        <h2 className="academic-heading" style={headingStyle(1)}>{content.bibliographyHeading}</h2>
        <ul className="academic-bibliography" style={{ listStyle: "none", margin: 0, padding: 0 }}>
          {content.bibliography.map((entry) => (
            <li
              key={entry.label}
              className="academic-bibliography-entry"
              style={
                spec.bibliographyHangingIndent
                  ? { textIndent: `-${pt(spec.bibliographyIndentPt)}`, paddingInlineStart: pt(spec.bibliographyIndentPt), marginBottom: pt(4) }
                  : { marginBottom: pt(4) }
              }
            >
              {entry.text}
            </li>
          ))}
        </ul>
        <p className="academic-bibliography-marker" style={{ ...paragraphStyle, fontSize: pt(8), color: spec.accentColorHex }}>
          {content.bibliographyMarker}
        </p>

        {spec.footnotesEnabled ? (
          <div className="academic-footnote" style={{ borderTop: `1px solid ${spec.accentColorHex}`, marginTop: pt(6), fontSize: pt(8) }}>
            {content.footnoteText}
          </div>
        ) : null}
      </main>

      {bodyFolio ? (
        <div className="academic-folio academic-folio-body" style={folioAlign[spec.pageNumberPosition] ?? folioAlign["bottom-center"]}>
          {content.pageWord} {bodyFolio}
        </div>
      ) : null}
    </div>
  );
}
