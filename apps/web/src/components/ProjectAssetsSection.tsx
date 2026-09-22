"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { File, LibraryBig } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { api, type Asset, type Project } from "@/lib/api";

export function ProjectAssetsSection({ project }: { project: Project }) {
  const { t, locale } = useLocale();
  const [assets, setAssets] = useState<Asset[]>([]);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    setLoading(true);
    try { setAssets((await api.listAssets(project.workspaceId, { projectId: project.id, status: "Active", pageSize: 6 })).items); }
    catch { setAssets([]); }
    finally { setLoading(false); }
  }, [project.id, project.workspaceId]);

  // Project assets are synchronized from the protected API when the project changes.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void load(); }, [load]);

  return <section className="account-card project-assets-section">
    <div className="card-title"><span className="card-title-icon"><LibraryBig size={17} /></span><div><h2>{t("assets.projectSectionTitle")}</h2><p>{t("assets.projectSectionSubtitle")}</p></div><Link className="secondary-button project-assets-link" href={`/assets?projectId=${project.id}`}>{t("assets.viewAll")}</Link></div>
    {loading ? <div className="generation-empty">{t("assets.loading")}</div> : assets.length === 0 ? <p className="usage-empty">{t("assets.projectEmpty")}</p> : <div className="project-assets-list">{assets.map((asset) => <Link href={`/assets?projectId=${project.id}`} key={asset.id}><span className="asset-type-icon"><File size={17} /></span><span><strong>{asset.name}</strong><small>{t(`assets.type.${asset.assetType}`)} · {new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(asset.createdAt))}</small></span></Link>)}</div>}
  </section>;
}
