"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { LibraryBig, Search, X } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { AssetCard, type AssetCardLabels } from "@/components/AssetCard";
import { api, type Asset, type AssetList, type AssetStatus, type AssetType, type Project } from "@/lib/api";

const assetTypes: AssetType[] = ["image", "document", "presentation", "video", "audio", "music", "research", "social", "file", "other"];

export function AssetsView({ initialProjectId, initialSearch, initialStatus }: { initialProjectId?: string; initialSearch?: string; initialStatus?: AssetStatus }) {
  const { workspace } = useAuth();
  const { t, locale } = useLocale();
  const [result, setResult] = useState<AssetList | null>(null);
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [search, setSearch] = useState(initialSearch ?? "");
  const [assetType, setAssetType] = useState<AssetType | "">("");
  const [projectId, setProjectId] = useState(initialProjectId ?? "");
  const [status, setStatus] = useState<AssetStatus>(initialStatus ?? "Active");
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<Asset | null>(null);
  const [editName, setEditName] = useState("");
  const [editDescription, setEditDescription] = useState("");
  const [editProjectId, setEditProjectId] = useState("");
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    if (!workspace) return;
    setLoading(true); setError("");
    try {
      const [assets, activeProjects, archivedProjects] = await Promise.all([
        api.listAssets(workspace.id, { search, assetType: assetType || undefined, projectId: projectId || undefined, status, page, pageSize: 12 }),
        api.listProjects(workspace.id, "Active"),
        api.listProjects(workspace.id, "Archived"),
      ]);
      setResult(assets);
      setProjects([...activeProjects, ...archivedProjects]);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("assets.loadError"));
    } finally { setLoading(false); }
  }, [assetType, page, projectId, search, status, t, workspace]);

  // Remote Asset Library state is synchronized after filters change.
  useEffect(() => { const timer = window.setTimeout(() => void load(), 250); return () => window.clearTimeout(timer); }, [load]);

  const labels = useMemo<AssetCardLabels>(() => ({ project: t("assets.project"), workspace: t("assets.workspaceLevel"), rename: t("assets.rename"), archive: t("assets.archive"), restore: t("assets.restore"), download: t("assets.download"), type: "" }), [t]);

  function startEdit(asset: Asset) {
    setEditing(asset); setEditName(asset.name); setEditDescription(asset.description ?? ""); setEditProjectId(asset.projectId ?? ""); setError("");
  }

  async function saveEdit(event: React.FormEvent) {
    event.preventDefault();
    if (!editing || !editName.trim()) return;
    setSaving(true); setError("");
    try {
      await api.updateAsset(editing.id, { name: editName.trim(), description: editDescription.trim() || null, projectId: editProjectId || null });
      setEditing(null);
      await load();
    } catch (caught) { setError(caught instanceof Error ? caught.message : t("assets.saveError")); }
    finally { setSaving(false); }
  }

  async function archive(asset: Asset) {
    if (!window.confirm(t("assets.archiveConfirm"))) return;
    try { await api.archiveAsset(asset.id); await load(); }
    catch (caught) { setError(caught instanceof Error ? caught.message : t("assets.archiveError")); }
  }

  async function restore(asset: Asset) {
    try { await api.restoreAsset(asset.id); await load(); }
    catch (caught) { setError(caught instanceof Error ? caught.message : t("assets.restoreError")); }
  }

  return <div className="assets-page">
    <section className="assets-hero">
      <div className="section-heading"><div><p className="section-eyebrow">{t("assets.eyebrow")}</p><h1>{t("assets.title")}</h1><p>{t("assets.subtitle")}</p></div><span className="assets-hero-icon"><LibraryBig size={24} /></span></div>
    </section>

    <section className="assets-toolbar" aria-label={t("assets.filters")}>
      <label className="asset-search"><Search size={16} /><input value={search} onChange={(event) => { setSearch(event.target.value); setPage(1); }} placeholder={t("assets.searchPlaceholder")} /></label>
      <select value={assetType} onChange={(event) => { setAssetType(event.target.value as AssetType | ""); setPage(1); }} aria-label={t("assets.typeFilter")}><option value="">{t("assets.allTypes")}</option>{assetTypes.map((type) => <option key={type} value={type}>{t(`assets.type.${type}`)}</option>)}</select>
      <select value={projectId} onChange={(event) => { setProjectId(event.target.value); setPage(1); }} aria-label={t("assets.projectFilter")}><option value="">{t("assets.allProjects")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select>
      <div className="asset-status-tabs"><button className={status === "Active" ? "is-active" : ""} onClick={() => { setStatus("Active"); setPage(1); }}>{t("assets.active")}</button><button className={status === "Archived" ? "is-active" : ""} onClick={() => { setStatus("Archived"); setPage(1); }}>{t("assets.archived")}</button></div>
    </section>

    {error && <div className="inline-error">{error}</div>}
    {loading ? <div className="assets-loading"><span className="loading-spinner" /><p>{t("assets.loading")}</p></div> : !result?.items.length ? <div className="assets-empty"><LibraryBig size={30} /><h2>{status === "Archived" ? t("assets.emptyArchivedTitle") : t("assets.emptyTitle")}</h2><p>{status === "Archived" ? t("assets.emptyArchivedDescription") : t("assets.emptyDescription")}</p></div> : <div className="asset-grid">{result.items.map((asset) => <AssetCard key={asset.id} asset={asset} locale={locale} labels={{ ...labels, type: t(`assets.type.${asset.assetType}`) }} onEdit={startEdit} onArchive={(item) => void archive(item)} onRestore={(item) => void restore(item)} />)}</div>}

    {result && result.totalPages > 1 && <div className="asset-pagination"><button className="secondary-button" disabled={page <= 1} onClick={() => setPage((value) => Math.max(1, value - 1))}>{t("assets.previous")}</button><span>{t("assets.page", { page: String(result.page), total: String(result.totalPages) })}</span><button className="secondary-button" disabled={page >= result.totalPages} onClick={() => setPage((value) => value + 1)}>{t("assets.next")}</button></div>}

    {editing && <div className="modal-backdrop" role="presentation"><form className="modal-card asset-edit-modal" onSubmit={(event) => void saveEdit(event)}><button type="button" className="modal-close" onClick={() => setEditing(null)} aria-label={t("common.close")}><X size={18} /></button><p className="section-eyebrow">{t("assets.editEyebrow")}</p><h2>{t("assets.editTitle")}</h2><label className="field"><span>{t("assets.name")}</span><input maxLength={255} required value={editName} onChange={(event) => setEditName(event.target.value)} /></label><label className="field"><span>{t("assets.description")}</span><textarea maxLength={2000} value={editDescription} onChange={(event) => setEditDescription(event.target.value)} /></label><label className="field"><span>{t("assets.project")}</span><select value={editProjectId} onChange={(event) => setEditProjectId(event.target.value)}><option value="">{t("assets.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label><div className="form-actions"><button type="button" className="secondary-button" onClick={() => setEditing(null)}>{t("common.cancel")}</button><button type="submit" className="primary-button" disabled={saving}>{saving ? t("common.saving") : t("common.saveChanges")}</button></div></form></div>}
  </div>;
}
