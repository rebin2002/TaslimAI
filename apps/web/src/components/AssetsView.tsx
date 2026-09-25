"use client";

import Link from "next/link";
import { useCallback, useEffect, useMemo, useState } from "react";
import { ArrowDownUp, Filter, Grid2X2, LayoutList, LibraryBig, Plus, Search, SlidersHorizontal, X } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { AssetCard, type AssetCardLabels } from "@/components/AssetCard";
import { AssetDetail, type AssetDetailLabels } from "@/components/AssetDetail";
import { api, type Asset, type AssetList, type AssetSort, type AssetStatus, type AssetType, type Project } from "@/lib/api";

type AssetCategory = { value: AssetType | ""; labelKey: string; query?: AssetType };
const assetCategories: AssetCategory[] = [
  { value: "", labelKey: "assets.category.all" },
  { value: "image", labelKey: "assets.category.images", query: "image" },
  { value: "video", labelKey: "assets.category.movies", query: "video" },
  { value: "audio", labelKey: "assets.category.voice", query: "audio" },
  { value: "music", labelKey: "assets.category.music", query: "music" },
  { value: "document", labelKey: "assets.category.documents", query: "document" },
  { value: "presentation", labelKey: "assets.category.presentations", query: "presentation" },
  { value: "research", labelKey: "assets.category.research", query: "research" },
  { value: "social", labelKey: "assets.category.social", query: "social" },
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
  const [filtersOpen, setFiltersOpen] = useState(false);

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
    detailEyebrow: t("assets.detailEyebrow"), close: t("common.close"), structuredTitle: t("assets.structuredTitle"), structuredDescription: t("assets.structuredDescription"), download: t("assets.download"), open: t("assets.open"), edit: t("assets.rename"), type: t("assets.typeLabel"), typeValue: selected ? t(`assets.type.${selected.assetType}`) : "", created: t("assets.created"), project: t("assets.project"), noProject: t("assets.noProject"), file: t("assets.file"), notAvailable: t("assets.notAvailable"), source: t("assets.source"), manual: t("assets.manualSource"), description: t("assets.description"), representations: t("assets.representations"), previewLabel: t("assets.structuredTitle"), summaryLabel: t("assets.description"), studios: { image: t("navigation.imageStudio"), document: t("navigation.documentStudio"), presentation: t("navigation.presentationStudio"), research: t("navigation.researchStudio"), social: t("navigation.socialStudio"), voice: t("navigation.voiceStudio"), music: t("navigation.musicStudio"), movie: t("navigation.movieStudio"), system: t("assets.systemSource") },
  }), [selected, t]);

  function setFilter<T>(setter: (value: T) => void, value: T) { setter(value); setPage(1); }
  function startEdit(asset: Asset) {
    setEditing(asset); setEditName(asset.name); setEditDescription(asset.description ?? ""); setEditProjectId(asset.projectId ?? ""); setError("");
  }
  function clearFilters() { setSearch(""); setCategory(""); setProjectId(""); setSort("recent"); setPage(1); }
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

  const activeFilters = [category !== "", projectId !== "", sort !== "recent", search.trim() !== ""].filter(Boolean).length;
  return <div className="assets-page">
    <section className="assets-hero">
      <div className="assets-hero-copy"><div className="assets-hero-kicker"><span className="assets-live-dot" />{t("assets.eyebrow")}</div><h1>{t("assets.title")}</h1><p>{t("assets.subtitle")}</p></div>
      <div className="assets-hero-mark"><LibraryBig size={23} /><span>{result?.totalCount ?? "—"}</span></div>
    </section>

    <section className="assets-toolbar" aria-label={t("assets.filters")}>
      <div className="assets-toolbar-top"><label className="asset-search"><Search size={16} /><input value={search} onChange={(event) => { setSearch(event.target.value); setPage(1); }} placeholder={t("assets.searchPlaceholder")} /><kbd>/</kbd></label><button type="button" className="mobile-filter-trigger" onClick={() => setFiltersOpen(true)}><Filter size={15} />{t("assets.filters")}{activeFilters > 0 && <b>{activeFilters}</b>}</button></div>
      <div className="asset-category-scroll" role="tablist" aria-label={t("assets.typeFilter")}>{assetCategories.map((item) => <button key={item.labelKey} type="button" role="tab" aria-selected={category === item.value} className={category === item.value ? "is-active" : ""} onClick={() => setFilter(setCategory, item.value)}>{t(item.labelKey)}</button>)}</div>
      <div className="assets-secondary-filters"><label className="assets-select"><ArrowDownUp size={14} /><select value={sort} onChange={(event) => setFilter(setSort, event.target.value as AssetSort)} aria-label={t("assets.sortLabel")}>{sortOptions.map((item) => <option key={item} value={item}>{t(`assets.sort.${item}`)}</option>)}</select></label><select value={projectId} onChange={(event) => setFilter(setProjectId, event.target.value)} aria-label={t("assets.projectFilter")}><option value="">{t("assets.allProjects")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select><div className="asset-view-toggle" role="group" aria-label={t("assets.viewLabel")}><button type="button" className={view === "grid" ? "is-active" : ""} onClick={() => setView("grid")} aria-label={t("assets.gridView")}><Grid2X2 size={16} /></button><button type="button" className={view === "list" ? "is-active" : ""} onClick={() => setView("list")} aria-label={t("assets.listView")}><LayoutList size={17} /></button></div></div>
      <div className="asset-status-tabs"><button type="button" className={status === "Active" ? "is-active" : ""} onClick={() => setFilter(setStatus, "Active")}>{t("assets.active")}</button><button type="button" className={status === "Archived" ? "is-active" : ""} onClick={() => setFilter(setStatus, "Archived")}>{t("assets.archived")}</button></div>
    </section>

    {error && <div className="inline-error">{error}</div>}
    <div className="assets-results-bar"><span>{result ? `${result.totalCount} ${t("assets.title").toLowerCase()}` : t("assets.loading")}</span><span className="assets-results-hint">{view === "grid" ? t("assets.gridView") : t("assets.listView")}</span></div>
    {loading ? <div className="assets-loading"><span className="loading-spinner" /><p>{t("assets.loading")}</p></div> : !result?.items.length ? <div className="assets-empty"><div className="assets-empty-icon"><Plus size={21} /></div><p className="section-eyebrow">{t("assets.eyebrow")}</p><h2>{status === "Archived" ? t("assets.emptyArchivedTitle") : t("assets.emptyTitle")}</h2><p>{status === "Archived" ? t("assets.emptyArchivedDescription") : t("assets.emptyDescription")}</p>{status !== "Archived" && <Link href="/create" className="primary-button"><Plus size={15} /> {t("navigation.create")}</Link>}{activeFilters > 0 && <button type="button" className="assets-clear-link" onClick={clearFilters}>{t("search.clear")}</button>}</div> : <div className={`asset-grid ${view === "list" ? "asset-list" : ""}`}>{result.items.map((asset) => <AssetCard key={asset.id} asset={asset} locale={locale} labels={{ ...labels, type: t(`assets.type.${asset.assetType}`) }} onOpen={setSelected} onEdit={startEdit} onArchive={(item) => void archive(item)} onRestore={(item) => void restore(item)} />)}</div>}
    {result && result.totalPages > 1 && <div className="asset-pagination"><button type="button" className="secondary-button" disabled={page <= 1} onClick={() => setPage((value) => Math.max(1, value - 1))}>{t("assets.previous")}</button><span>{t("assets.page", { page: String(result.page), total: String(result.totalPages) })}</span><button type="button" className="secondary-button" disabled={page >= result.totalPages} onClick={() => setPage((value) => value + 1)}>{t("assets.next")}</button></div>}

    {selected && <AssetDetail asset={selected} locale={locale} labels={detailLabels} onClose={() => setSelected(null)} onEdit={(asset) => { setSelected(null); startEdit(asset); }} />}
    {filtersOpen && <div className="asset-filter-backdrop" role="presentation" onMouseDown={(event) => { if (event.currentTarget === event.target) setFiltersOpen(false); }}><section className="asset-filter-sheet" role="dialog" aria-modal="true" aria-labelledby="asset-filter-title"><div className="asset-filter-sheet-header"><div><p className="section-eyebrow">{t("assets.filters")}</p><h2 id="asset-filter-title">{t("assets.typeFilter")}</h2></div><button type="button" className="modal-close" onClick={() => setFiltersOpen(false)} aria-label={t("common.close")}><X size={18} /></button></div><div className="asset-filter-sheet-body"><div className="asset-filter-group"><span>{t("assets.typeFilter")}</span><div className="asset-filter-options">{assetCategories.map((item) => <button key={item.labelKey} type="button" className={category === item.value ? "is-active" : ""} onClick={() => setFilter(setCategory, item.value)}>{t(item.labelKey)}</button>)}</div></div><label className="asset-filter-field"><span>{t("assets.projectFilter")}</span><select value={projectId} onChange={(event) => setFilter(setProjectId, event.target.value)}><option value="">{t("assets.allProjects")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label><label className="asset-filter-field"><span>{t("assets.sortLabel")}</span><select value={sort} onChange={(event) => setFilter(setSort, event.target.value as AssetSort)}>{sortOptions.map((item) => <option key={item} value={item}>{t(`assets.sort.${item}`)}</option>)}</select></label></div><div className="asset-filter-sheet-actions"><button type="button" className="secondary-button" onClick={clearFilters}>{t("search.clear")}</button><button type="button" className="primary-button" onClick={() => setFiltersOpen(false)}><SlidersHorizontal size={14} />{t("common.saveChanges")}</button></div></section></div>}
    {editing && <div className="modal-backdrop" role="presentation"><form className="modal-card asset-edit-modal" onSubmit={(event) => void saveEdit(event)}><button type="button" className="modal-close" onClick={() => setEditing(null)} aria-label={t("common.close")}><X size={18} /></button><p className="section-eyebrow">{t("assets.editEyebrow")}</p><h2>{t("assets.editTitle")}</h2><label className="field"><span>{t("assets.name")}</span><input maxLength={255} required value={editName} onChange={(event) => setEditName(event.target.value)} /></label><label className="field"><span>{t("assets.description")}</span><textarea maxLength={2000} value={editDescription} onChange={(event) => setEditDescription(event.target.value)} /></label><label className="field"><span>{t("assets.project")}</span><select value={editProjectId} onChange={(event) => setEditProjectId(event.target.value)}><option value="">{t("assets.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label><div className="form-actions"><button type="button" className="secondary-button" onClick={() => setEditing(null)}>{t("common.cancel")}</button><button type="submit" className="primary-button" disabled={saving}>{saving ? t("common.saving") : t("common.saveChanges")}</button></div></form></div>}
  </div>;
}
