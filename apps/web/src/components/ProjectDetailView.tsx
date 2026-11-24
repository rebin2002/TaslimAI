"use client";
import { localeTag } from "@/lib/i18n";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { Activity, Archive, ArrowLeft, ArrowUpRight, BarChart3, CalendarDays, FileText, FolderOpen, LibraryBig, MessageSquare, Pencil, Plus, RotateCcw, Sparkles } from "lucide-react";
import { useParams, useRouter } from "next/navigation";
import { api, type Project, type ProjectInput, type ProjectOverview } from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";
import { ProjectForm } from "@/components/ProjectForm";
import { ProjectContextEditor } from "@/components/ProjectContextEditor";
import { ProjectFilesSection } from "@/components/ProjectFilesSection";
import { ProjectAssetsSection } from "@/components/ProjectAssetsSection";
import { resolveProjectDetailSnapshotAfterFailure } from "@/lib/projectDetailAccessState";
import { canApplyProjectDetailSnapshot, hasProjectDetailAccessEnded } from "@/lib/projectDetailAccessOrdering";

const projectActions = [
  ["/chat", "projects.newConversation", MessageSquare, "project-action-chat"],
  ["/create/image", "projects.studio.image", Sparkles, "project-action-image"],
  ["/create/document", "projects.studio.document", FileText, "project-action-document"],
  ["/create/research", "projects.studio.research", LibraryBig, "project-action-research"],
] as const;

export function ProjectDetailView() {
  const { t, locale } = useLocale();
  const params = useParams<{ projectId: string }>();
  const router = useRouter();
  const [overview, setOverview] = useState<ProjectOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [formOpen, setFormOpen] = useState(false);
  const [error, setError] = useState("");
  const [loadAttempt, setLoadAttempt] = useState(0);
  const overviewRequest = useRef<{ key: string; promise: Promise<ProjectOverview>; epoch: number } | null>(null);
  // How many definitive "access ended" answers this mount has seen. It is the
  // ordering marker behind the role badge: every overview read records the
  // value it was issued under and may only commit while that value still
  // holds, so a membership snapshot the server captured before a revocation
  // can never re-pin the badge after one. A read issued afterwards sees the
  // new value and paints normally, which is what keeps the retry, the
  // language-pack refresh and a re-admitted role working.
  const accessEndedEpoch = useRef(0);
  // The message that answer carried. A retired response must leave the page
  // exactly where the revocation left it: the effect that re-issued it has
  // already cleared the banner, so without this the member would land on the
  // not-found branch, which has no retry affordance at all.
  const accessEndedMessage = useRef("");
  // The project the route currently names, kept live so continuations that
  // outlive their originating page can be retired. The load effect fences
  // itself with a per-run `active` flag; the mutation continuations have no
  // effect run of their own, so they consult this instead: a mutation answer
  // about a project the member navigated away from must not act on the page
  // at all — not its snapshot, not the access-ended epoch, not the error banner.
  const routeProjectId = useRef(params.projectId);

  useEffect(() => {
    let active = true;
    routeProjectId.current = params.projectId;
    const requestKey = `${params.projectId}:${loadAttempt}`;
    const cachedRequest = overviewRequest.current?.key === requestKey ? overviewRequest.current : null;
    // The access-ended epoch this read was issued under. It is taken when the
    // request is created and kept with it in the single-slot cache, so a
    // refresh re-issued after a revocation still carries the epoch of the
    // membership it asked for, not the epoch of the answer that superseded it.
    const accessEpoch = cachedRequest ? cachedRequest.epoch : accessEndedEpoch.current;
    // Loading remote project state is an external synchronization.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setLoading(true); setError("");
    const request = cachedRequest ? cachedRequest.promise : api.getProjectOverview(params.projectId);
    if (!cachedRequest) overviewRequest.current = { key: requestKey, promise: request, epoch: accessEpoch };
    void request
      .then((next) => {
        if (!active) return;
        if (canApplyProjectDetailSnapshot(accessEpoch, accessEndedEpoch.current)) { setOverview(next); return; }
        // Retired: the page stays exactly where the definitive answer left it.
        setError((current) => current || accessEndedMessage.current);
      })
      .catch((caught) => {
        if (!active) return;
        const message = caught instanceof Error ? caught.message : t("projects.loadError");
        // Ordering, not disposition: a definitive access answer also retires
        // every snapshot issued before it, so the one on screen and the ones
        // still on the wire are dropped by the same event that clears this
        // read's own result. Transient failures leave the epoch untouched.
        if (hasProjectDetailAccessEnded(caught)) { accessEndedEpoch.current += 1; accessEndedMessage.current = message; }
        // A definitive access answer outranks the cached membership snapshot:
        // clear it so the workspace member role badge fails closed with the
        // retry affordance, while a transient load keeps the last view.
        if (resolveProjectDetailSnapshotAfterFailure(caught) === "invalidate") setOverview(null);
        setError(message);
      })
      .finally(() => {
        if (overviewRequest.current?.promise === request) overviewRequest.current = null;
        if (active) setLoading(false);
      });
    return () => { active = false; };
  }, [loadAttempt, params.projectId, t]);

  function replaceProject(project: Project) {
    setOverview((current) => current ? { ...current, project } : current);
  }

  async function archive() {
    if (!overview || !window.confirm(t("projects.archiveConfirm"))) return;
    // Captured at click time: the answer, whatever it carries, is only about
    // this project. If the route has moved on by the time it lands, retire it.
    const targetProjectId = overview.project.id;
    try {
      const archived = await api.archiveProject(targetProjectId);
      if (routeProjectId.current === targetProjectId) replaceProject(archived);
    }
    catch (caught) {
      if (routeProjectId.current !== targetProjectId) return;
      // 401/403/404 here means the server no longer resolves this project for
      // this principal; a "removed" member must not keep a live role badge.
      // This is also the answer that usually confirms the revocation, so it
      // must retire the overview read that was still in flight at the time.
      const message = caught instanceof Error ? caught.message : t("projects.saveError");
      if (hasProjectDetailAccessEnded(caught)) { accessEndedEpoch.current += 1; accessEndedMessage.current = message; }
      if (resolveProjectDetailSnapshotAfterFailure(caught) === "invalidate") setOverview(null);
      setError(message);
    }
  }

  async function restore() {
    if (!overview) return;
    const targetProjectId = overview.project.id;
    try {
      const restored = await api.restoreProject(targetProjectId);
      if (routeProjectId.current === targetProjectId) replaceProject(restored);
    }
    catch (caught) {
      if (routeProjectId.current !== targetProjectId) return;
      const message = caught instanceof Error ? caught.message : t("projects.saveError");
      if (hasProjectDetailAccessEnded(caught)) { accessEndedEpoch.current += 1; accessEndedMessage.current = message; }
      if (resolveProjectDetailSnapshotAfterFailure(caught) === "invalidate") setOverview(null);
      setError(message);
    }
  }

  function startConversation() {
    if (!overview) return;
    router.push(`/chat?projectId=${encodeURIComponent(overview.project.id)}`);
  }

  if (loading) return <div className="loading-state"><span className="loading-spinner" /></div>;
  if (error && !overview) return <div className="inline-error" role="alert"><span>{error || t("projects.notFound")}</span> <button type="button" className="text-button" onClick={() => setLoadAttempt((attempt) => attempt + 1)}>{t("error.retry")}</button></div>;
  if (!overview) return <div className="inline-error">{t("projects.notFound")}</div>;

  const { project, workspace, counts, conversations, recentActivity } = overview;
  const type = t(`project.type.${project.type.toLowerCase()}`);
  const workspaceTypeKey = workspace.type.toLowerCase() === "business" ? "projects.businessWorkspace" : "projects.personalWorkspace";
  const roleKey = `projects.role.${workspace.role.toLowerCase()}`;

  return <div className="project-detail-page">
    <Link href="/projects" className="back-link"><ArrowLeft size={15} /> {t("projects.backToProjects")}</Link>

    <header className="detail-header project-detail-hero">
      <div className="project-hero-copy">
        <div className="project-hero-label"><span className="detail-icon"><FolderOpen size={22} /></span><span className="project-type-pill">{type}</span></div>
        <p className="section-eyebrow">{t("projects.overviewEyebrow")}</p>
        <h1>{project.name}</h1>
        <p>{project.description || t("projects.noDescription")}</p>
      </div>
      <div className="detail-actions">
        <button className="secondary-button" onClick={() => setFormOpen(true)}><Pencil size={15} /> {t("projects.edit")}</button>
        {project.status === "Active" ? <button className="secondary-button" onClick={() => void archive()}><Archive size={15} /> {t("projects.archive")}</button> : <button className="primary-button" onClick={() => void restore()}><RotateCcw size={15} /> {t("projects.restore")}</button>}
      </div>
    </header>

    <div className="detail-meta project-detail-meta">
      <span><CalendarDays size={15} /> {t("projects.updated")} {formatDate(project.updatedAt, locale)}</span>
      <span><Sparkles size={15} /> {t(`projects.status.${project.status.toLowerCase()}`)}</span>
      <span><FolderOpen size={15} /> {t("projects.metadataId", { id: project.id.slice(0, 8) })}</span>
    </div>
    {error && <div className="inline-error" role="alert">{error}</div>}

    <section className="project-overview-hero project-overview-intro">
      <div><p className="section-eyebrow">{t("projects.overviewEyebrow")}</p><h2>{t("projects.overviewTitle")}</h2><p>{t("projects.overviewDescription")}</p></div>
      <div className="project-workspace-summary"><span className="workspace-banner-icon"><BarChart3 size={17} /></span><div><small>{t(workspaceTypeKey)}</small><strong>{workspace.name}</strong></div><span className="workspace-role">{t(roleKey)}</span></div>
    </section>

    <div className="project-stat-grid project-stat-grid-premium" aria-label={t("projects.overviewTitle")}>
      <OverviewStat icon={<FileText size={18} />} label={t("projects.stats.files")} value={counts.files} />
      <OverviewStat icon={<LibraryBig size={18} />} label={t("projects.stats.assets")} value={counts.assets} />
      <OverviewStat icon={<MessageSquare size={18} />} label={t("projects.stats.conversations")} value={counts.conversations} />
      <OverviewStat icon={<Activity size={18} />} label={t("projects.stats.activity")} value={counts.activity} />
    </div>

    <section className="project-quick-create project-action-surface account-card">
      <div className="card-title"><span className="card-title-icon"><Plus size={17} /></span><div><h2>{t("projects.quickCreateTitle")}</h2><p>{t("projects.quickCreateDescription")}</p></div></div>
      <div className="project-action-grid">
        <button type="button" className="project-action project-action-continue" aria-label={t("projects.conversationsTitle")} onClick={startConversation}><span className="project-action-icon"><MessageSquare size={16} /></span><span><strong>{t("projects.newConversation")}</strong><small>{t("projects.conversationsDescription")}</small></span><ArrowUpRight size={15} /></button>
        {projectActions.slice(1).map(([href, label, Icon, className]) => <Link key={href} href={`${href}?projectId=${project.id}`} aria-label={t(label)} className={`project-action ${className}`}><span className="project-action-icon"><Icon size={16} /></span><span><strong>{t(label)}</strong><small>{t("projects.quickCreateDescription")}</small></span><ArrowUpRight size={15} /></Link>)}
      </div>
    </section>

    <div className="project-overview-grid project-overview-grid-premium">
      <section className="account-card project-activity-card">
        <div className="card-title"><span className="card-title-icon teal"><Activity size={17} /></span><div><h2>{t("projects.activityTitle")}</h2><p>{t("projects.activityDescription")}</p></div></div>
        {recentActivity.length === 0 ? <p className="usage-empty">{t("projects.noActivity")}</p> : <div className="project-activity-list">{recentActivity.map((item) => <article className="project-activity-row" key={item.jobId}><span className={`activity-status-dot activity-status-${item.status.toLowerCase()}`} /><div><strong>{item.title}</strong><small>{t(`activity.type.${item.jobType}`)} · {formatDate(item.createdAt, locale)}</small></div>{item.assetId ? <Link className="text-link" href={`/assets?projectId=${project.id}`}>{t("projects.openResult")}</Link> : <span className="project-activity-status">{t(`activity.status.${item.status.toLowerCase()}`)}</span>}</article>)}</div>}
      </section>
      <section className="account-card project-conversations-card">
        <div className="card-title"><span className="card-title-icon"><MessageSquare size={17} /></span><div><h2>{t("projects.conversationsTitle")}</h2><p>{t("projects.conversationsDescription")}</p></div><button className="secondary-button project-new-chat" onClick={startConversation}><Plus size={14} /> {t("projects.newConversation")}</button></div>
        {conversations.length === 0 ? <p className="usage-empty">{t("projects.noConversations")}</p> : <div className="project-conversations-list">{conversations.map((conversation) => <Link href={`/chat/${conversation.id}`} key={conversation.id}><MessageSquare size={15} /><span><strong>{conversation.title}</strong><small>{formatDate(conversation.updatedAt, locale)}</small></span><ArrowUpRight size={14} /></Link>)}</div>}
      </section>
    </div>

    <ProjectContextEditor project={project} onSaved={replaceProject} />
    <ProjectFilesSection project={project} />
    <ProjectAssetsSection project={project} />
    <section className="project-metadata-card account-card"><div><p className="section-eyebrow">{t("projects.metadataEyebrow")}</p><h2>{t("projects.metadataTitle")}</h2></div><dl><div><dt>{t("projects.metadataCreated")}</dt><dd>{formatDate(project.createdAt, locale)}</dd></div><div><dt>{t("projects.metadataUpdated")}</dt><dd>{formatDate(project.updatedAt, locale)}</dd></div><div><dt>{t("projects.metadataWorkspace")}</dt><dd>{workspace.name}</dd></div><div><dt>{t("projects.metadataRole")}</dt><dd>{t(roleKey)}</dd></div></dl></section>
    {formOpen && <ProjectForm project={project} onClose={() => setFormOpen(false)} onSubmit={async (input: ProjectInput) => { const updated = await api.updateProject(project.id, input); replaceProject(updated); setFormOpen(false); }} />}
  </div>;
}

function OverviewStat({ icon, label, value }: { icon: React.ReactNode; label: string; value: number }) {
  return <div className="project-stat"><span>{icon}</span><strong>{value}</strong><small>{label}</small></div>;
}

function formatDate(value: string, locale: string) {
  return new Intl.DateTimeFormat(localeTag(locale), { dateStyle: "medium" }).format(new Date(value));
}
