import { academicCopy, type AcademicCopyLocale } from "./academicTemplateCopy";
import { AcademicTemplatePreview } from "./AcademicTemplatePreview";
import type { AcademicTemplateCatalog, AcademicTemplateConfiguration } from "./academicTemplateTypes";

export interface AcademicTemplatePreviewCardProps {
  catalog: AcademicTemplateCatalog;
  configuration: AcademicTemplateConfiguration;
  locale?: AcademicCopyLocale;
  selected?: boolean;
  onSelect?: (configuration: AcademicTemplateConfiguration) => void;
}

/**
 * One selectable design: the deterministic synthetic mini-page on top, and beneath it
 * the variant chips, the short design fingerprint (first 12 hex characters — enough to
 * prove distinctness, nothing more) and an accessible select action. No provider, model
 * or cost data can appear here; the card only ever reads the design spec.
 */
export function AcademicTemplatePreviewCard({
  catalog,
  configuration,
  locale = "en",
  selected = false,
  onSelect,
}: AcademicTemplatePreviewCardProps) {
  const { spec } = configuration;
  return (
    <article
      className="academic-template-card"
      data-configuration-id={configuration.configurationId}
      data-design-hash={configuration.designHash}
      aria-labelledby={`atp-title-${configuration.designHash}`}
      style={{ border: `1px solid ${selected ? "#2563eb" : "#d4d4d8"}`, borderRadius: 12, overflow: "hidden", background: "#fff" }}
    >
      <div className="academic-template-card-preview" style={{ maxHeight: 320, overflow: "hidden" }}>
        <AcademicTemplatePreview catalog={catalog} spec={spec} configurationId={configuration.configurationId} />
      </div>
      <div className="academic-template-card-meta" style={{ padding: "12px 14px" }}>
        <h3 id={`atp-title-${configuration.designHash}`} style={{ margin: 0, fontSize: 15 }}>
          {configuration.displayName}
        </h3>
        <p className="academic-template-card-variants" style={{ margin: "6px 0" }}>
          <span className="sr-only">{academicCopy(locale, "variantsLabel")}: </span>
          {Object.entries(configuration.variantSelection).map(([axis, value]) => (
            <span key={axis} className="academic-variant-chip" style={{ display: "inline-block", border: "1px solid #e4e4e7", borderRadius: 999, padding: "1px 8px", margin: "0 4px 4px 0", fontSize: 12 }}>
              {axis}: {value}
            </span>
          ))}
        </p>
        <p className="academic-template-card-hash" style={{ margin: "4px 0", fontSize: 12, color: "#52525b" }}>
          {academicCopy(locale, "designHashLabel")}: <code>{configuration.designHash}</code>
        </p>
        <button
          type="button"
          className="academic-template-card-select"
          aria-pressed={selected}
          aria-label={academicCopy(locale, "previewAria", { name: configuration.displayName })}
          onClick={() => onSelect?.(configuration)}
          style={{ marginTop: 6, fontSize: 13, padding: "6px 12px", borderRadius: 8, cursor: "pointer" }}
        >
          {selected ? "\u2713 " : ""}
          {academicCopy(locale, selected ? "selectedDesignLabel" : "useDesignLabel")}
        </button>
      </div>
    </article>
  );
}
