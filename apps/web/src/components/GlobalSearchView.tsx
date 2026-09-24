"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { FileText, FolderOpen, LibraryBig, MessageCircle, Search, Sparkles, Upload, X } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useLocale } from "@/components/LocaleProvider";
import { api, type GlobalSearchGroup, type GlobalSearchResult, type GlobalSearchResultType } from "@/lib/api";

const groupOrder: GlobalSearchResultType[] = ["projects", "conversations", "assets", "files", "generation"];

const icons: Record<GlobalSearchResultType, typeof FolderOpen> = {
  projects: FolderOpen,
  conversations: MessageCircle,
  assets: LibraryBig,
  files: Upload,
  generation: Sparkles,
};

function formatDate(value: string | null, locale: string) {
  if (!value) return "";
  return new Intl.DateTimeFormat(locale, { month: "short", day: "numeric", year: "numeric" }).format(new Date(value));
}

function destination(result: GlobalSearchResult) {
  if (result.type === "projects") return `/projects/${result.id}`;
  if (result.type === "conversations") return `/chat/${result.id}`;
  if (result.type === "assets") return `/assets?search=${encodeURIComponent(result.title)}&status=${result.status ?? "Active"}`;
  if (result.type === "files") {
    if (result.projectId) return `/projects/${result.projectId}`;
    if (result.conversationId) return `/chat/${result.conversationId}`;
    return "/create/document";
  }
  if (result.assetId) return `/assets?search=${encodeURIComponent(result.title)}&status=Active`;
  if (result.projectId) return `/projects/${result.projectId}`;
  return "/notifications";
}

export function GlobalSearchView() {
  const { t, locale } = useLocale();
  const router = useRouter();
  const searchParams = useSearchParams();
  const initialQuery = searchParams.get("q") ?? "";
  const [query, setQuery] = useState(initialQuery);
  const [submittedQuery, setSubmittedQuery] = useState(initialQuery.trim());
  const [groups, setGroups] = useState<GlobalSearchGroup[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [loading, setLoading] = useState(Boolean(initialQuery.trim()));
  const [error, setError] = useState("");

  const runSearch = useCallback(async (value: string) => {
    const next = value.trim();
    if (!next) {
      setGroups([]); setTotalCount(0); setLoading(false); setError("");
      return;
    }
    setLoading(true); setError("");
    try {
      const response = await api.search(next);
      setGroups(response.groups); setTotalCount(response.totalCount); setSubmittedQuery(response.query);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("search.loadError"));
    } finally { setLoading(false); }
  }, [t]);

  useEffect(() => {
    const timer = window.setTimeout(() => void runSearch(initialQuery), initialQuery.trim() ? 180 : 0);
    return () => window.clearTimeout(timer);
  }, [initialQuery, runSearch]);

  useEffect(() => {
    const handleShortcut = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        document.getElementById("global-search-input")?.focus();
      }
    };
    window.addEventListener("keydown", handleShortcut);
    return () => window.removeEventListener("keydown", handleShortcut);
  }, []);

  function submit(event: React.FormEvent) {
    event.preventDefault();
    const next = query.trim();
    router.push(next ? `/search?q=${encodeURIComponent(next)}` : "/search");
    void runSearch(next);
  }

  function clear() {
    setQuery("");
    setSubmittedQuery("");
    router.push("/search");
    setGroups([]); setTotalCount(0); setError("");
  }

  const orderedGroups = useMemo(() => groupOrder.map((type) => groups.find((group) => group.type === type)).filter(Boolean) as GlobalSearchGroup[], [groups]);

  return <div className="global-search-page">
    <section className="global-search-hero">
      <p className="section-eyebrow">{t("search.eyebrow")}</p>
      <h1>{t("search.title")}</h1>
      <p>{t("search.subtitle")}</p>
      <form className="global-search-form" onSubmit={submit} role="search">
        <Search size={19} aria-hidden="true" />
        <input id="global-search-input" autoFocus value={query} onChange={(event) => setQuery(event.target.value)} placeholder={t("search.placeholder")} aria-label={t("navigation.search")} />
        {query && <button type="button" className="global-search-clear" onClick={clear} aria-label={t("search.clear")}><X size={16} /></button>}
        <kbd>⌘ K</kbd>
        <button type="submit" className="primary-button">{t("search.submit")}</button>
      </form>
      <p className="global-search-hint">{t("search.hint")}</p>
    </section>

    {error && <div className="inline-error" role="alert">{error}</div>}
    {loading ? <div className="loading-state"><span className="loading-spinner" /><span className="sr-only">{t("search.results")}</span></div> : !submittedQuery ? <section className="global-search-empty"><span className="global-search-empty-icon"><Search size={25} /></span><h2>{t("search.emptyTitle")}</h2><p>{t("search.emptyDescription")}</p></section> : orderedGroups.length === 0 ? <section className="global-search-empty"><span className="global-search-empty-icon"><Search size={25} /></span><h2>{t("search.noResultsTitle")}</h2><p>{t("search.noResultsDescription")}</p></section> : <section className="global-search-results" aria-live="polite"><div className="global-search-results-heading"><div><p className="section-eyebrow">{t("search.results")}</p><h2>{t("search.resultCount", { count: String(totalCount) })}</h2></div><span className="global-search-query">{submittedQuery}</span></div>{orderedGroups.map((group) => <SearchGroup key={group.type} group={group} locale={locale} t={t} />)}</section>}
  </div>;
}

function SearchGroup({ group, locale, t }: { group: GlobalSearchGroup; locale: string; t: (key: string, variables?: Record<string, string>) => string }) {
  const Icon = icons[group.type];
  return <section className="global-search-group" aria-labelledby={`search-group-${group.type}`}><div className="global-search-group-heading"><span className="global-search-group-icon"><Icon size={17} /></span><h2 id={`search-group-${group.type}`}>{t(`search.group.${group.type}`)}</h2><span>{group.count}</span></div><div className="global-search-result-list">{group.items.map((result) => <SearchResult key={`${result.type}-${result.id}`} result={result} locale={locale} t={t} />)}</div></section>;
}

function SearchResult({ result, locale, t }: { result: GlobalSearchResult; locale: string; t: (key: string, variables?: Record<string, string>) => string }) {
  const Icon = icons[result.type];
  const metadata = result.type === "files" ? t("search.file", { type: result.metadata ?? "" }) : result.metadata ? (result.type === "projects" ? t("search.type", { type: result.metadata }) : result.metadata) : null;
  return <Link href={destination(result)} className="global-search-result" aria-label={`${t("search.open")}: ${result.title}`}><span className="global-search-result-icon"><Icon size={17} /></span><span className="global-search-result-copy"><strong>{result.title}</strong><span className="global-search-result-details">{result.projectName ? t("search.project", { name: result.projectName }) : metadata}{result.projectName && metadata ? ` · ${metadata}` : ""}</span><small>{result.status ? t("search.status", { status: result.status }) : ""}{result.status && result.updatedAt ? " · " : ""}{formatDate(result.updatedAt ?? result.createdAt, locale)}</small></span><span className="global-search-open"><FileText size={15} /></span></Link>;
}
