"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { Grid2X2, List, LibraryBig, Search, SlidersHorizontal, X } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { AssetCard, type AssetCardLabels } from "@/components/AssetCard";
import { AssetDetail, type AssetDetailLabels } from "@/components/AssetDetail";
import { api, type Asset, type AssetList, type AssetSort, type AssetStatus, type AssetType, type Project } from "@/lib/api";

type AssetCategory = { value: AssetType | ""; labelKey: string; query?: AssetType };
const assetCategories: AssetCategory[] = [
  { value: "", labelKey: "assets.category.all" },
  { value: "image", labelKey: "assets.category.images", query: "image" },
  { value: "document", labelKey: "assets.category.documents", query: "document" },
  { value: "presentation", labelKey: "assets.category.presentations", query: "presentation" },
  { value: "research", labelKey: "assets.category.research", query: "research" },
  { value: "social", labelKey: "assets.category.social", query: "social" },
  { value: "audio", labelKey: "assets.category.voice", query: "audio" },
  { value: "music", labelKey: "assets.category.music", query: "music" },
  { value: "video", labelKey: "assets.category.movies", query: "video" },
];
const sortOptions: AssetSort[] = ["recent", "oldest", "name", "size"];

export function AssetsView({ initialProjectId, initialSearch, initialStatus }: { initialProjectId?: string; initialSearch?: string; initialStatus?: AssetStatus }) {
  const { workspace } = useAuth();
  const { t, locale } = useLocale();
  const [result, setResult] = useState<AssetList | null>(null);
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [search, setSearch] = useState(initialSearch ?? "");
  const [category, setCategory] = useState<AssetType | "">("");
  const [projectId, setProjectId] = useState(initialProjectId ?? "");
  const [status, setStatus] = useState<AssetStatus>(initialStatus ?? "Active");
  const [sort, setSort] = useState<AssetSort>("recent");
  const [view, setView] = useState<"grid" | "list">("grid");
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<Asset | null>(null);
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
        api.listAssets(workspace.id, { search, assetType: assetCategories.find((item) => item.value === category)?.query, projectId: projectId || undefined, status, sort, page, pageSize: 12 }),
        api.listProjects(workspace.id, "Active"),
        api.listProjects(workspace.id, "Archived"),
      ]);
      setResult(assets);
      setProjects([...activeProjects, ...archivedProjects]);
      setSelected((current) => current ? assets.items.find((item) => item.id === current.id) ?? current : current);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("assets.loadError"));
    } finally { setLoading(false); }
  }, [category, page, projectId, search, sort, status, t, workspace]);

  useEffect(() => { const timer = window.setTimeout(() => void load(), 250); return () => window.clearTimeout(timer); }, [load]);

  const labels = useMemo<AssetCardLabels>(() => ({
    project: t("assets.project"), workspace: t("assets.workspaceLevel"), rename: t("assets.rename"), archive: t("assets.archive"), restore: t("assets.restore"), download: t("assets.download"), open: t("assets.open"), type: "", fileUnavailable: t("assets.fileUnavailable"),
  }), [t]);
  const detailLabels = useMemo<AssetDetailLabels>(() => ({
    detailEyebrow: t("assets.detailEyebrow"), close: t("common.close"), structuredTitle: t("assets.structuredTitle"), structuredDescription: t("assets.structuredDescription"), download: t("assets.download"), open: t("assets.open"), edit: t("assets.rename"), type: t("assets.typeLabel"), typeValue: selected ? t(`assets.type.${selected.assetType}`) : "", created: t("assets.created"), project: t("assets.project"), noProject: t("assets.noProject"), file: t("assets.file"), notAvailable: t("assets.notAvailable"), source: t("assets.source"), manual: t("assets.manualSource"), description: t("assets.description"), representations: t("assets.representations"), studios: { image: t("navigation.imageStudio"), document: t("navigation.documentStudio"), presentation: t("navigation.presentationStudio"), research: t("navigation.researchStudio"), social: t("navigation.socialStudio"), voice: t("navigation.voiceStudio"), music: t("navigation.musicStudio"), movie: t("navigation.movieStudio"), system: t("assets.systemSource") },
  }), [selected, t]);

  function startEdit(asset: Asset) {
    setEditing(asset); setEditName(asset.name); setEditDescription(asset.description ?? ""); setEditProjectId(asset.projectId ?? ""); setError("");
  }

  async function saveEdit(event: React.FormEvent) {
    event.preventDefault();
    if (!editing || !editName.trim()) return;
    setSaving(true); setError("");
    try {
      const updated = await api.updateAsset(editing.id, { name: editName.trim(), description: editDescription.trim() || null, projectId: editProjectId || null });
      setEditing(null); setSelected((current) => current?.id === updated.id ? updated : current); await load();
    } catch (caught) { setError(caught instanceof Error ? caught.message : t("assets.saveError")); }
    finally { setSaving(false); }
  }

  async function archive(asset: Asset) {
    if (!window.confirm(t("assets.archiveConfirm"))) return;
    try { const updated = await api.archiveAsset(asset.id); setSelected((current) => current?.id === updated.id ? updated : current); await load(); }
    catch (caught) { setError(caught instanceof Error ? caught.message : t("assets.archiveError")); }
  }

  async function restore(asset: Asset) {
    try { const updated = await api.restoreAsset(asset.id); setSelected((current) => current?.id === updated.id ? updated : current); await load(); }
    catch (caught) { setError(caught instanceof Error ? caught.message : t("assets.restoreError")); }
  }

  return <div className="assets-page">
    <section className="assets-hero">
      <div><p className="section-eyebrow">{t("assets.eyebrow")}</p><h1>{t("assets.title")}</h1><p>{t("assets.subtitle")}</p></div>
      <span className="assets-hero-icon"><LibraryBig size={24} /></span>
    </section>

    <section className="assets-toolbar" aria-label={t("assets.filters")}>
      <label className="asset-search"><Search size={16} /><input value={search} onChange={(event) => { setSearch(event.target.value); setPage(1); }} placeholder={t("assets.searchPlaceholder")} /><kbd>/</kbd></label>
      <div className="asset-category-scroll" role="tablist" aria-label={t("assets.typeFilter")}>{assetCategories.map((item) => <button key={item.labelKey} type="button" role="tab" aria-selected={category === item.value} className={category === item.value ? "is-active" : ""} onClick={() => { setCategory(item.value); setPage(1); }}>{t(item.labelKey)}</button>)}</div>
      <div className="assets-secondary-filters"><label className="assets-select"><SlidersHorizontal size={14} /><select value={sort} onChange={(event) => { setSort(event.target.value as AssetSort); setPage(1); }} aria-label={t("assets.sortLabel")}>{sortOptions.map((item) => <option key={item} value={item}>{t(`assets.sort.${item}`)}</option>)}</select></label><select value={projectId} onChange={(event) => { setProjectId(event.target.value); setPage(1); }} aria-label={t("assets.projectFilter")}><option value="">{t("assets.allProjects")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select><div className="asset-view-toggle" role="group" aria-label={t("assets.viewLabel")}><button type="button" className={view === "grid" ? "is-active" : ""} onClick={() => setView("grid")} aria-label={t("assets.gridView")}><Grid2X2 size={16} /></button><button type="button" className={view === "list" ? "is-active" : ""} onClick={() => setView("list")} aria-label={t("assets.listView")}><List size={17} /></button></div></div>
      <div className="asset-status-tabs"><button type="button" className={status === "Active" ? "is-active" : ""} onClick={() => { setStatus("Active"); setPage(1); }}>{t("assets.active")}</button><button type="button" className={status === "Archived" ? "is-active" : ""} onClick={() => { setStatus("Archived"); setPage(1); }}>{t("assets.archived")}</button></div>
    </section>

    {error && <div className="inline-error">{error}</div>}
    {loading ? <div className="assets-loading"><span className="loading-spinner" /><p>{t("assets.loading")}</p></div> : !result?.items.length ? <div className="assets-empty"><LibraryBig size={30} /><h2>{status === "Archived" ? t("assets.emptyArchivedTitle") : t("assets.emptyTitle")}</h2><p>{status === "Archived" ? t("assets.emptyArchivedDescription") : t("assets.emptyDescription")}</p></div> : <div className={`asset-grid ${view === "list" ? "asset-list" : ""}`}>{result.items.map((asset) => <AssetCard key={asset.id} asset={asset} locale={locale} labels={{ ...labels, type: t(`assets.type.${asset.assetType}`) }} onOpen={setSelected} onEdit={startEdit} onArchive={(item) => void archive(item)} onRestore={(item) => void restore(item)} />)}</div>}
    {result && result.totalPages > 1 && <div className="asset-pagination"><button type="button" className="secondary-button" disabled={page <= 1} onClick={() => setPage((value) => Math.max(1, value - 1))}>{t("assets.previous")}</button><span>{t("assets.page", { page: String(result.page), total: String(result.totalPages) })}</span><button type="button" className="secondary-button" disabled={page >= result.totalPages} onClick={() => setPage((value) => value + 1)}>{t("assets.next")}</button></div>}

    {selected && <AssetDetail asset={selected} locale={locale} labels={detailLabels} onClose={() => setSelected(null)} onEdit={(asset) => { setSelected(null); startEdit(asset); }} />}
    {editing && <div className="modal-backdrop" role="presentation"><form className="modal-card asset-edit-modal" onSubmit={(event) => void saveEdit(event)}><button type="button" className="modal-close" onClick={() => setEditing(null)} aria-label={t("common.close")}><X size={18} /></button><p className="section-eyebrow">{t("assets.editEyebrow")}</p><h2>{t("assets.editTitle")}</h2><label className="field"><span>{t("assets.name")}</span><input maxLength={255} required value={editName} onChange={(event) => setEditName(event.target.value)} /></label><label className="field"><span>{t("assets.description")}</span><textarea maxLength={2000} value={editDescription} onChange={(event) => setEditDescription(event.target.value)} /></label><label className="field"><span>{t("assets.project")}</span><select value={editProjectId} onChange={(event) => setEditProjectId(event.target.value)}><option value="">{t("assets.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label><div className="form-actions"><button type="button" className="secondary-button" onClick={() => setEditing(null)}>{t("common.cancel")}</button><button type="submit" className="primary-button" disabled={saving}>{saving ? t("common.saving") : t("common.saveChanges")}</button></div></form></div>}
  </div>;
}
