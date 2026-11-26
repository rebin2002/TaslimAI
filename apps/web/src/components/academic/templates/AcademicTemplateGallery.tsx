import { useMemo, useState } from "react";
import { academicCopy, type AcademicCopyLocale } from "./academicTemplateCopy";
import { countConfigurations, distinctDesignHashes, filterConfigurations } from "./academicTemplateCatalog";
import { AcademicTemplatePreviewCard } from "./AcademicTemplatePreviewCard";
import type { AcademicFilterCriteria, AcademicTemplateCatalog, AcademicTemplateConfiguration } from "./academicTemplateTypes";

const PAGE_SIZE = 24;

export interface AcademicTemplateGalleryProps {
  catalog: AcademicTemplateCatalog;
  locale?: AcademicCopyLocale;
  /** Live API catalogue may replace the bundled snapshot; the gallery stays pure over whatever it is given. */
  onSelect?: (configuration: AcademicTemplateConfiguration) => void;
}

interface SelectFilterProps {
  locale: AcademicCopyLocale;
  label: string;
  value: string;
  options: readonly string[];
  onChange: (value: string) => void;
}

function SelectFilter({ locale, label, value, options, onChange }: SelectFilterProps) {
  return (
    <label className="academic-filter" style={{ display: "flex", flexDirection: "column", gap: 4, fontSize: 13 }}>
      <span>{label}</span>
      <select
        value={value}
        onChange={(event) => onChange(event.target.value)}
        style={{ padding: "6px 8px", borderRadius: 8, border: "1px solid #d4d4d8", minWidth: 140 }}
      >
        <option value="">{academicCopy(locale, "optionAny")}</option>
        {options.map((option) => (
          <option key={option} value={option}>
            {option}
          </option>
        ))}
      </select>
    </label>
  );
}

/**
 * The full design library gallery over a catalog snapshot: data-driven facets (document
 * kind, citation style, language with the friendly "ckb" alias, paper, direction, free
 * text) filtered client-side with the same semantics as the API facets, an aria-live
 * result count, chunked rendering so 230+ designs stay responsive, and fixed
 * integrity/privacy/snapshot notes. The component holds no network state — the
 * container decides whether the data comes from `/api/academic-templates` or the
 * committed snapshot.
 */
export function AcademicTemplateGallery({ catalog, locale = "en", onSelect }: AcademicTemplateGalleryProps) {
  const [criteria, setCriteria] = useState<AcademicFilterCriteria>({});
  const [visibleCount, setVisibleCount] = useState(PAGE_SIZE);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const rtl = locale === "ckb" || locale === "ar";

  const results = useMemo(() => filterConfigurations(catalog, criteria), [catalog, criteria]);
  const totalMatches = useMemo(() => countConfigurations(catalog, criteria), [catalog, criteria]);
  const distinctHashes = useMemo(() => new Set(results.map((entry) => entry.designHash)).size, [results]);
  const visible = results.slice(0, visibleCount);

  const update = (patch: Partial<AcademicFilterCriteria>) => {
    setCriteria((current) => ({ ...current, ...patch }));
    setVisibleCount(PAGE_SIZE);
  };

  const handleSelect = (configuration: AcademicTemplateConfiguration) => {
    setSelectedId(configuration.configurationId);
    onSelect?.(configuration);
  };

  return (
    <section
      className="academic-template-gallery"
      dir={rtl ? "rtl" : "ltr"}
      lang={locale === "ckb" ? "ku" : locale}
      aria-busy="false"
    >
      <header>
        <h1 style={{ margin: 0, fontSize: 22 }}>{academicCopy(locale, "galleryTitle")}</h1>
        <p style={{ margin: "6px 0 14px", color: "#3f3f46", maxWidth: 720 }}>{academicCopy(locale, "gallerySubtitle")}</p>
      </header>

      <div className="academic-template-filters" style={{ display: "flex", flexWrap: "wrap", gap: 12, alignItems: "flex-end", marginBottom: 14 }}>
        <label style={{ display: "flex", flexDirection: "column", gap: 4, fontSize: 13, flex: "1 1 220px" }}>
          <span>{academicCopy(locale, "searchLabel")}</span>
          <input
            type="search"
            value={criteria.query ?? ""}
            placeholder={academicCopy(locale, "searchPlaceholder")}
            onChange={(event) => update({ query: event.target.value })}
            style={{ padding: "6px 8px", borderRadius: 8, border: "1px solid #d4d4d8" }}
          />
        </label>
        <SelectFilter locale={locale} label={academicCopy(locale, "filterKind")} value={criteria.documentKind ?? ""} options={catalog.documentKinds} onChange={(value) => update({ documentKind: value || undefined })} />
        <SelectFilter locale={locale} label={academicCopy(locale, "filterCitation")} value={criteria.citationStyle ?? ""} options={catalog.citationStyles} onChange={(value) => update({ citationStyle: value || undefined })} />
        <SelectFilter locale={locale} label={academicCopy(locale, "filterLanguage")} value={criteria.language ?? ""} options={[...catalog.languages, "ckb"]} onChange={(value) => update({ language: value || undefined })} />
        <SelectFilter locale={locale} label={academicCopy(locale, "filterPaper")} value={criteria.paperFormat ?? ""} options={catalog.paperFormats} onChange={(value) => update({ paperFormat: value || undefined })} />
        <label style={{ display: "flex", flexDirection: "column", gap: 4, fontSize: 13 }}>
          <span>{academicCopy(locale, "filterDirection")}</span>
          <select
            value={criteria.direction ?? ""}
            onChange={(event) => update({ direction: event.target.value || undefined })}
            style={{ padding: "6px 8px", borderRadius: 8, border: "1px solid #d4d4d8", minWidth: 140 }}
          >
            <option value="">{academicCopy(locale, "optionAny")}</option>
            <option value="ltr">{academicCopy(locale, "directionLtr")}</option>
            <option value="rtl">{academicCopy(locale, "directionRtl")}</option>
          </select>
        </label>
      </div>

      <p className="academic-template-results-count" role="status" aria-live="polite" style={{ margin: "0 0 12px", fontSize: 14, color: "#52525b" }}>
        {academicCopy(locale, "resultsCount", { count: totalMatches })}
        {totalMatches > 0 && distinctHashes === totalMatches ? null : ` (${distinctHashes} unique)`}
      </p>

      <div className="academic-template-card-grid" style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(300px, 1fr))", gap: 16 }}>
        {visible.map((configuration) => (
          <AcademicTemplatePreviewCard
            key={configuration.configurationId}
            catalog={catalog}
            configuration={configuration}
            locale={locale}
            selected={selectedId === configuration.configurationId}
            onSelect={onSelect ? handleSelect : undefined}
          />
        ))}
      </div>

      {visible.length < results.length ? (
        <button
          type="button"
          className="academic-template-load-more"
          onClick={() => setVisibleCount((count) => count + PAGE_SIZE)}
          style={{ marginTop: 16, padding: "8px 16px", borderRadius: 8, cursor: "pointer" }}
        >
          {academicCopy(locale, "loadMore")}
        </button>
      ) : results.length > 0 ? (
        <p className="academic-template-all-loaded" style={{ marginTop: 16, color: "#52525b" }}>{academicCopy(locale, "allLoaded")}</p>
      ) : null}

      <footer className="academic-template-notes" style={{ marginTop: 24, display: "grid", gap: 8, color: "#3f3f46", fontSize: 13 }}>
        <p className="academic-integrity-note">{academicCopy(locale, "integrityNote")}</p>
        <p className="academic-snapshot-note">{academicCopy(locale, "snapshotNote")}</p>
        <p className="academic-privacy-note">{academicCopy(locale, "privacyNote")}</p>
        <p style={{ color: "#71717a" }}>
          {catalog.baseTemplateCount} base templates · {catalog.distinctConfigurationCount} configurations ·{" "}
          {distinctDesignHashes(catalog).length} distinct designs ({catalog.catalogVersion})
        </p>
      </footer>
    </section>
  );
}
