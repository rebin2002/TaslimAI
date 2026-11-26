"use client";

import { useEffect, useState } from "react";
import { API_URL } from "@/lib/apiBase";
import { bundledCatalog } from "./academicTemplateCatalog";
import { AcademicTemplateGallery } from "./AcademicTemplateGallery";
import type { AcademicTemplateCatalog, AcademicTemplateConfiguration } from "./academicTemplateTypes";

export interface AcademicTemplateGalleryContainerProps {
  locale?: "en" | "ckb" | "ar";
  /** Called with the versioned template id + configuration id the export pipeline consumes. */
  onConfigurationSelected?: (configuration: AcademicTemplateConfiguration) => void;
}

/**
 * Thin client shell around the gallery. It tries the authenticated API catalog first
 * (so server-side taxonomy changes appear without a web redeploy) and falls back to
 * the committed snapshot — CI's parity test guarantees the fallback is byte-identical
 * to the registry, so the two paths render the same 230 designs. The request carries
 * only cookies; no provider, model, routing or backend data is ever requested or shown.
 */
export function AcademicTemplateGalleryContainer({ locale = "en", onConfigurationSelected }: AcademicTemplateGalleryContainerProps) {
  const [catalog, setCatalog] = useState<AcademicTemplateCatalog>(bundledCatalog);
  const [source, setSource] = useState<"bundled" | "api" | "loading">("loading");
  const [selected, setSelected] = useState<AcademicTemplateConfiguration | null>(null);

  useEffect(() => {
    let cancelled = false;
    const controller = new AbortController();
    void (async () => {
      try {
        const response = await fetch(`${API_URL}/api/academic-templates`, {
          credentials: "include",
          signal: controller.signal,
          headers: { accept: "application/json" },
        });
        if (!response.ok) throw new Error(`catalog request failed: ${response.status}`);
        const live = (await response.json()) as AcademicTemplateCatalog;
        // Only trust a live catalog that still guarantees the distinct-design floor.
        if (live.distinctConfigurationCount < 150 || live.definitionVersion !== bundledCatalog.definitionVersion) {
          throw new Error("live catalog rejected");
        }
        if (!cancelled) {
          setCatalog(live);
          setSource("api");
        }
      } catch {
        if (!cancelled) {
          setCatalog(bundledCatalog);
          setSource("bundled");
        }
      }
    })();
    return () => {
      cancelled = true;
      controller.abort();
    };
  }, []);

  return (
    <div data-academic-catalog-source={source}>
      <AcademicTemplateGallery
        catalog={catalog}
        locale={locale}
        onSelect={(configuration) => {
          setSelected(configuration);
          onConfigurationSelected?.(configuration);
        }}
      />
      {selected ? (
        <p className="academic-template-selected-summary" role="status" aria-live="polite" style={{ marginTop: 12, fontSize: 13 }}>
          {selected.templateId} · {selected.configurationId} · {selected.designHash}
        </p>
      ) : null}
    </div>
  );
}
