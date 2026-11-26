"use client";
import { localeTag } from "@/lib/i18n";
/* eslint-disable @next/next/no-img-element -- previews use authenticated API URLs with session cookies. */

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { ArrowUpRight, LibraryBig } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { api, type Asset, type Project } from "@/lib/api";
import { assetFileUrl } from "@/lib/apiBase";
import { AssetTypeIcon } from "@/components/AssetCard";

/* Authenticated asset previews intentionally use the API URL rather than a public image path. */
export function shouldApplyProjectAssetsResponse(active: boolean, requestId: number, currentRequestId: number) {
  return active && requestId === currentRequestId;
}

export function ProjectAssetsSection({ project }: { project: Project }) {
  const { t, locale } = useLocale();
  const [assets, setAssets] = useState<Asset[]>([]);
  const [loading, setLoading] = useState(true);
  const requestSequence = useRef(0);

  useEffect(() => {
    const requestId = ++requestSequence.current;
    let active = true;
    // Loading state is synchronized with the protected API request when the project changes.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setLoading(true);
    void api.listAssets(project.workspaceId, { projectId: project.id, status: "Active", pageSize: 6, sort: "recent" })
      .then((result) => {
        if (shouldApplyProjectAssetsResponse(active, requestId, requestSequence.current)) setAssets(result.items);
      })
      .catch(() => {
        if (shouldApplyProjectAssetsResponse(active, requestId, requestSequence.current)) setAssets([]);
      })
      .finally(() => {
        if (shouldApplyProjectAssetsResponse(active, requestId, requestSequence.current)) setLoading(false);
      });
    return () => { active = false; };
  }, [project.id, project.workspaceId]);

  return <section className="account-card project-assets-section">
    <div className="card-title"><span className="card-title-icon"><LibraryBig size={17} /></span><div><h2>{t("assets.projectSectionTitle")}</h2><p>{t("assets.projectSectionSubtitle")}</p></div><Link className="secondary-button project-assets-link" href={`/assets?projectId=${project.id}`}>{t("assets.viewAll")} <ArrowUpRight size={14} /></Link></div>
    {loading ? <div className="generation-empty">{t("assets.loading")}</div> : assets.length === 0 ? <p className="usage-empty">{t("assets.projectEmpty")}</p> : <div className="project-assets-grid">{assets.map((asset) => <Link href={`/assets?projectId=${project.id}`} className="project-asset-tile" key={asset.id}>
      <span className={`project-asset-preview project-asset-preview-${asset.assetType}`}>{asset.hasFile && asset.canPreview && asset.assetType === "image" ? <img src={assetFileUrl(asset.id, true)} alt="" loading="lazy" /> : <AssetTypeIcon type={asset.assetType} size={23} />}</span>
      <span className="project-asset-copy"><strong>{asset.name}</strong><small>{t(`assets.type.${asset.assetType}`)} · {new Intl.DateTimeFormat(localeTag(locale), { dateStyle: "medium" }).format(new Date(asset.createdAt))}</small></span>
      <ArrowUpRight size={14} className="project-asset-arrow" />
    </Link>)}</div>}
  </section>;
}
