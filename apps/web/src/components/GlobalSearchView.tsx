"use client";
import { localeTag } from "@/lib/i18n";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { FileText, FolderOpen, LibraryBig, MessageCircle, RefreshCw, Search, Sparkles, Upload, X, XCircle } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useLocale } from "@/components/LocaleProvider";
import { api, type GlobalSearchGroup, type GlobalSearchResult, type GlobalSearchResultType } from "@/lib/api";
import { globalSearchResultDestination } from "@/lib/searchNavigation";
import { appendGlobalSearchGroups } from "@/lib/searchPagination";
import { createRequestSequencer } from "@/lib/searchRequestLifecycle";
import {
  clearedSearchResults,
  searchStatusAnnouncement,
  searchSurfaceStatus,
  settleSearchCancellation,
  settleSearchFailure,
  type SearchSettlementFields,
} from "@/lib/searchSurfaceState";

const groupOrder: GlobalSearchResultType[] = ["projects", "conversations", "assets", "files", "generation"];

type Translate = (key: string, variables?: Record<string, string>) => string;

const icons: Record<GlobalSearchResultType, typeof FolderOpen> = {
  projects: FolderOpen,
  conversations: MessageCircle,
  assets: LibraryBig,
  files: Upload,
  generation: Sparkles,
};

function formatDate(value: string | null, locale: string) {
  if (!value) return "";
  return new Intl.DateTimeFormat(localeTag(locale), { month: "short", day: "numeric", year: "numeric" }).format(new Date(value));
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
  const [page, setPage] = useState(1);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(Boolean(initialQuery.trim()));
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState("");
  const requestSequence = useMemo(() => createRequestSequencer(), []);
  const pendingSearchTimer = useRef<number | null>(null);

  const clearPendingSearch = useCallback(() => {
    if (pendingSearchTimer.current !== null) {
      window.clearTimeout(pendingSearchTimer.current);
      pendingSearchTimer.current = null;
    }
  }, []);

  /**
   * Writes one of {@link settleSearchCancellation} / {@link settleSearchFailure}'s
   * answers to every part of the surface, so retiring a search cannot half-clear it -
   * the old hand-written reset left `loading` behind, and the read it had fenced was
   * the only thing that would ever have cleared it.
   */
  const applySettlement = useCallback((settlement: SearchSettlementFields) => {
    setGroups(settlement.groups ?? clearedSearchResults.groups);
    setTotalCount(settlement.totalCount ?? clearedSearchResults.totalCount);
    setPage(settlement.page ?? clearedSearchResults.page);
    setHasMore(settlement.hasMore ?? clearedSearchResults.hasMore);
    if (settlement.submittedQuery !== undefined) setSubmittedQuery(settlement.submittedQuery);
    if (settlement.loading !== undefined) setLoading(settlement.loading);
    if (settlement.loadingMore !== undefined) setLoadingMore(settlement.loadingMore);
    if (settlement.error !== undefined) setError(settlement.error);
  }, []);

  const runSearch = useCallback(async (value: string, nextPage = 1, append = false) => {
    const next = value.trim();
    const requestId = requestSequence.begin();
    if (!next) {
      // Nothing was searched, so the surface must not keep answering the term it just dropped.
      applySettlement(settleSearchCancellation());
      return;
    }
    if (append) setLoadingMore(true);
    else setLoading(true);
    setError("");
    try {
      const response = await api.search(next, nextPage);
      if (!requestSequence.isCurrent(requestId)) return;
      setGroups((previous) => append ? appendGlobalSearchGroups(previous, response.groups) : response.groups);
      setTotalCount(response.totalCount);
      setSubmittedQuery(response.query);
      setPage(response.page);
      setHasMore(response.hasMore);
    } catch (caught) {
      if (!requestSequence.isCurrent(requestId)) return;
      const settlement = settleSearchFailure(next, append);
      if (!settlement.retainResults) applySettlement({ ...settlement.results, submittedQuery: settlement.attemptedQuery });
      setError(caught instanceof Error ? caught.message : t("search.loadError"));
    } finally {
      if (requestSequence.isCurrent(requestId)) {
        setLoading(false);
        setLoadingMore(false);
      }
    }
  }, [applySettlement, requestSequence, t]);

  useEffect(() => {
    clearPendingSearch();
    requestSequence.begin();
    pendingSearchTimer.current = window.setTimeout(() => {
      pendingSearchTimer.current = null;
      void runSearch(initialQuery);
    }, initialQuery.trim() ? 180 : 0);
    return clearPendingSearch;
  }, [clearPendingSearch, initialQuery, requestSequence, runSearch]);

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
    clearPendingSearch();
    if (next === initialQuery.trim()) {
      void runSearch(next);
    } else {
      // The rows on screen were read for a different term, and the read that could
      // replace them has not even started yet, so retire them now rather than
      // leaving them to be re-labelled by whichever response arrives last.
      requestSequence.begin();
      applySettlement({ ...clearedSearchResults, submittedQuery: next, loading: Boolean(next), loadingMore: false, error: "" });
      router.push(next ? `/search?q=${encodeURIComponent(next)}` : "/search");
    }
  }

  function clear() {
    clearPendingSearch();
    // Fencing the read is only half of cancelling: the fenced read applies no state
    // of its own, so the busy flag it raised has to be retired by this gesture.
    requestSequence.begin();
    applySettlement(settleSearchCancellation());
    setQuery("");
    router.push("/search");
  }

  function loadMore() {
    if (!submittedQuery || loading || loadingMore || !hasMore) return;
    void runSearch(submittedQuery, page + 1, true);
  }

  const orderedGroups = useMemo(() => groupOrder.map((type) => groups.find((group) => group.type === type)).filter(Boolean) as GlobalSearchGroup[], [groups]);

  const status = searchSurfaceStatus({ loading, error, submittedQuery, groupCount: orderedGroups.length });
  const announcement = searchStatusAnnouncement(status, submittedQuery, totalCount);

  return <div className="global-search-page" aria-busy={loading || loadingMore}>
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

    {/*
     * One polite live region for the whole surface, mounted for as long as the page
     * is. Each outcome used to carry its own semantics - the busy spinner announced
     * the results heading, the empty panels announced nothing, and the results
     * section was itself the live region - so every transition *out* of results
     * unmounted the only announcing element and left the reader with no status at
     * all. Keeping a single concise sentence here means cancelling, finding nothing,
     * and finding something are all reported as changes inside a region that stays.
     */}
    <p className="sr-only" role="status">{announcement ? t(announcement.key, announcement.variables) : ""}</p>
    {error && status !== "failed" && <div className="inline-error" role="alert">{error}</div>}
    {status === "searching" ? <div className="loading-state" aria-hidden="true"><span className="loading-spinner" /></div>
      : status === "failed" ? <GlobalSearchFailurePanel query={submittedQuery} error={error} t={t} onRetry={() => void runSearch(submittedQuery)} />
      : status === "idle" ? <section className="global-search-empty"><span className="global-search-empty-icon"><Search size={25} /></span><h2>{t("search.emptyTitle")}</h2><p>{t("search.emptyDescription")}</p></section>
      : status === "noResults" ? <section className="global-search-empty"><span className="global-search-empty-icon"><Search size={25} /></span><h2>{t("search.noResultsTitle")}</h2><p>{t("search.noResultsDescription")}</p></section>
      : <section className="global-search-results"><div className="global-search-results-heading"><div><p className="section-eyebrow">{t("search.results")}</p><h2>{t("search.resultCount", { count: String(totalCount) })}</h2></div><span className="global-search-query">{submittedQuery}</span></div>{orderedGroups.map((group) => <SearchGroup key={group.type} group={group} locale={locale} t={t} />)}{hasMore && <div className="global-search-pagination"><button type="button" className="secondary-button" onClick={loadMore} disabled={loadingMore} aria-busy={loadingMore}>{loadingMore ? t("search.loadingMore") : t("search.loadMore")}</button></div>}</section>}
  </div>;
}

/**
 * A search that never completed is not a search that found nothing, so it gets
 * its own panel: the failure is read out assertively, the term it belongs to is
 * named, and the retry is offered next to the box that still holds it. This is
 * the same distinction the Research report's evidence panel was fixed to make.
 */
export function GlobalSearchFailurePanel({ query, error, onRetry, t }: { query: string; error: string; onRetry: () => void; t: Translate }) {
  return <div className="inline-error" role="alert">
    <XCircle size={15} /> <strong>{t("search.failedTitle")}</strong>
    <p>{error}</p>
    <p>{t("search.failedHint", { query })}</p>
    <button className="secondary-button" type="button" onClick={onRetry}><RefreshCw size={15} /> {t("search.retry")}</button>
  </div>;
}

function SearchGroup({ group, locale, t }: { group: GlobalSearchGroup; locale: string; t: (key: string, variables?: Record<string, string>) => string }) {
  const Icon = icons[group.type];
  return <section className="global-search-group" aria-labelledby={`search-group-${group.type}`}><div className="global-search-group-heading"><span className="global-search-group-icon"><Icon size={17} /></span><h2 id={`search-group-${group.type}`}>{t(`search.group.${group.type}`)}</h2><span>{group.count}</span></div><div className="global-search-result-list">{group.items.map((result) => <SearchResult key={`${result.type}-${result.id}`} result={result} locale={locale} t={t} />)}</div></section>;
}

function SearchResult({ result, locale, t }: { result: GlobalSearchResult; locale: string; t: (key: string, variables?: Record<string, string>) => string }) {
  const Icon = icons[result.type];
  const metadata = result.type === "files" ? t("search.file", { type: result.metadata ?? "" }) : result.metadata ? (result.type === "projects" ? t("search.type", { type: result.metadata }) : result.metadata) : null;
  return <Link href={globalSearchResultDestination(result)} className="global-search-result" aria-label={`${t("search.open")}: ${result.title}`}><span className="global-search-result-icon"><Icon size={17} /></span><span className="global-search-result-copy"><strong>{result.title}</strong><span className="global-search-result-details">{result.projectName ? t("search.project", { name: result.projectName }) : metadata}{result.projectName && metadata ? ` · ${metadata}` : ""}</span><small>{result.status ? t("search.status", { status: result.status }) : ""}{result.status && result.updatedAt ? " · " : ""}{formatDate(result.updatedAt ?? result.createdAt, locale)}</small></span><span className="global-search-open"><FileText size={15} /></span></Link>;
}
