"use client";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useMemo, useState } from "react";
import { ArrowRight, ArrowUpRight, FolderKanban, LibraryBig, LoaderCircle, MessageSquare, Sparkles } from "lucide-react";
import { ProtectedPage } from "@/components/ProtectedPage";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { StudioChooser } from "@/components/StudioChooser";
import { api, type ActivityItem, type Asset, type Conversation, type Project } from "@/lib/api";
import { buildHomeRecentItems, hasInFlightActivity, type HomeRecentItem } from "@/lib/homeDashboardState";

const localeMap = { en: "en-US", ar: "ar", ku: "ku-Arab" } as const;
type DashboardData = { projects: Project[]; conversations: Conversation[]; activity: ActivityItem[]; assets: Asset[] };

function formatDate(value: string, locale: keyof typeof localeMap) {
  return new Intl.DateTimeFormat(localeMap[locale], { month: "short", day: "numeric" }).format(new Date(value));
}

export default function HomePage() {
  return <ProtectedPage><HomeDashboard /></ProtectedPage>;
}

function HomeDashboard() {
  const { t, locale } = useLocale();
  const { user, workspace } = useAuth();
  const router = useRouter();
  const [idea, setIdea] = useState("");
  const [asking, setAsking] = useState(false);
  const [dashboard, setDashboard] = useState<DashboardData | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [askError, setAskError] = useState("");

  const loadDashboard = useCallback(async () => {
    if (!workspace) return;
    setLoading(true);
    try {
      const [projects, conversations, activityResult, assetsResult] = await Promise.all([
        api.listProjects(workspace.id, "Active"),
        api.listConversations(workspace.id),
        api.listActivity(workspace.id, 1, 12),
        api.listAssets(workspace.id, { status: "Active", page: 1, pageSize: 12 }),
      ]);
      setDashboard({ projects, conversations, activity: activityResult.items, assets: assetsResult.items });
      setLoadError("");
    } catch (caught) {
      setLoadError(caught instanceof Error ? caught.message : t("home.loadError"));
    } finally {
      setLoading(false);
    }
  }, [t, workspace]);

  // Fetch workspace-scoped records after authentication state is available.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void loadDashboard(); }, [loadDashboard]);
  const recentItems = useMemo(() => dashboard ? buildHomeRecentItems(dashboard, 6) : [], [dashboard]);
  const hasWorkingGeneration = hasInFlightActivity(dashboard?.activity ?? []);

  async function askTaslim() {
    const prompt = idea.trim();
    if (!prompt) { router.push("/chat"); return; }
    if (!workspace) return;
    setAsking(true);
    setAskError("");
    try {
      const conversation = await api.createConversation(workspace.id);
      await api.sendMessage(conversation.id, prompt);
      router.push(`/chat/${conversation.id}`);
    } catch {
      setAskError(t("home.askError"));
    } finally {
      setAsking(false);
    }
  }

  return (
    <div className="home-dashboard">
      <section className="home-welcome" aria-labelledby="home-title">
        <div className="home-welcome-copy">
          <p className="section-eyebrow"><span className="home-presence-dot" /> {t("home.dashboardEyebrow")}</p>
          <h1 id="home-title">{t("home.welcome", { name: user?.displayName ?? "" })}</h1>
          <p>{t("home.dashboardSubtitle")}</p>
        </div>
        <Link href="/chat" className="home-chat-entry home-chat-entry-quiet">
          <span className="home-chat-entry-icon"><MessageSquare size={18} /></span>
          <span><strong>{t("home.chatCta")}</strong><small>{t("home.chatEntryHint")}</small></span>
          <ArrowUpRight size={16} />
        </Link>
      </section>

      <section className="home-creation-console" aria-labelledby="home-create-title">
        <div className="home-console-heading">
          <span className="home-console-icon"><Sparkles size={18} /></span>
          <div><p className="section-eyebrow">{t("home.createPanelLabel")}</p><h2 id="home-create-title">{t("home.title")}</h2><p>{t("home.consoleHint")}</p></div>
        </div>
        <div className="home-idea-input-wrap">
          <input className="home-idea-input" value={idea} onChange={(event) => setIdea(event.target.value)} onKeyDown={(event) => { if (event.key === "Enter") void askTaslim(); }} placeholder={t("home.searchPlaceholder")} aria-label={t("home.searchPlaceholder")} />
          <button type="button" className="home-idea-submit" onClick={() => void askTaslim()} disabled={asking}>
            {asking ? <LoaderCircle className="home-button-spinner" size={17} /> : <><span>{t("home.askTaslim")}</span><ArrowRight size={16} /></>}
          </button>
        </div>
        {askError && <p className="home-console-error" role="alert">{askError}</p>}
        <div className="home-console-footer"><span>{t("home.consolePrivacy")}</span><Link href="/create" className="home-browse-link">{t("home.browseStudios")} <ArrowUpRight size={14} /></Link></div>
      </section>

      <section className="home-continue" aria-labelledby="continue-work-title">
        <div className="home-section-heading home-continue-heading">
          <div><p className="section-eyebrow">{hasWorkingGeneration ? t("home.workInProgress") : t("home.recent")}</p><h2 id="continue-work-title">{t("home.continueTitle")}</h2><p>{t("home.continueDescription")}</p></div>
          <div className="home-continue-links"><Link href="/projects">{t("navigation.projects")}</Link><Link href="/chat">{t("navigation.chat")}</Link><Link href="/notifications">{t("navigation.activity")}</Link><Link href="/assets">{t("navigation.assets")}</Link></div>
        </div>
        {loadError && <div className="inline-error home-dashboard-error" role="alert">{loadError}</div>}
        {loading ? <div className="home-recent-loading"><LoaderCircle className="home-button-spinner" size={21} /><span>{t("home.loadingWork")}</span></div> : recentItems.length ? <div className="home-recent-grid">{recentItems.map((item) => <RecentWorkCard key={item.id} item={item} locale={locale} t={t} />)}</div> : <div className="home-empty-work"><FolderKanban size={24} /><div><h3>{t("home.noWorkTitle")}</h3><p>{t("home.noWorkDescription")}</p></div><Link href="/create" className="secondary-button">{t("navigation.create")} <ArrowUpRight size={14} /></Link></div>}
      </section>

      <section className="home-studios" aria-label={t("home.createPanelLabel")}><StudioChooser compact /></section>
    </div>
  );
}

function RecentWorkCard({ item, locale, t }: { item: HomeRecentItem; locale: "en" | "ar" | "ku"; t: (key: string, variables?: Record<string, string>) => string }) {
  const iconByKind = { project: FolderKanban, conversation: MessageSquare, activity: Sparkles, asset: LibraryBig };
  const Icon = iconByKind[item.kind];
  const kindLabel = item.kind === "project" ? t("navigation.projects") : item.kind === "conversation" ? t("chat.title") : item.kind === "activity" ? t(`activity.type.${item.jobType ?? "other"}`) : t(`assets.type.${item.assetType ?? "other"}`);
  const statusLabel = item.status ? t(`activity.status.${item.status.toLowerCase()}`) : null;
  const statusClass = item.status ? `home-status-${item.status.toLowerCase()}` : "";
  return <Link href={item.href} className="home-recent-card">
    <span className={`home-recent-icon home-recent-icon-${item.kind}`}><Icon size={17} /></span>
    <span className="home-recent-content"><span className="home-recent-meta"><span>{kindLabel}</span>{statusLabel && <span className={`home-work-status ${statusClass}`}>{statusLabel}</span>}</span><strong>{item.title}</strong><small>{formatDate(item.timestamp, locale)}</small>{item.status && (item.status === "Queued" || item.status === "Running") && <span className="home-progress"><span style={{ width: `${Math.max(0, Math.min(100, item.progressPercent ?? 0))}%` }} /></span>}</span>
    <ArrowUpRight className="home-recent-arrow" size={15} />
  </Link>;
}
