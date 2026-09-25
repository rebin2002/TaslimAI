"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { Archive, ArrowUpRight, BriefcaseBusiness, CalendarDays, FolderOpen, Pencil, Plus, RotateCcw } from "lucide-react";
import { useRouter, useSearchParams } from "next/navigation";
import { api, type Project, type ProjectInput } from "@/lib/api";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { ProjectForm } from "@/components/ProjectForm";

const typeKey = (type: string) => `project.type.${type.toLowerCase()}`;

function formatDate(value: string, locale: string) {
  return new Intl.DateTimeFormat(locale, { month: "short", day: "numeric", year: "numeric" }).format(new Date(value));
}

function projectAccent(type: string) {
  const normalized = type.toLowerCase();
  if (["marketing", "business"].includes(normalized)) return "project-accent-coral";
  if (["research", "education"].includes(normalized)) return "project-accent-teal";
  if (["movie", "development"].includes(normalized)) return "project-accent-violet";
  return "project-accent-blue";
}

export function ProjectsView() {
  const { workspace } = useAuth();
  const { t, locale } = useLocale();
  const searchParams = useSearchParams();
  const router = useRouter();
  const [status, setStatus] = useState<"Active" | "Archived">("Active");
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);
  const [formOpen, setFormOpen] = useState(() => searchParams.get("create") === "1");
  const [editing, setEditing] = useState<Project | undefined>();
  const [error, setError] = useState("");
  const workspaceTypeLabel = workspace?.type === "Business" ? t("projects.businessWorkspace") : t("projects.personalWorkspace");
  const workspaceRoleLabel = workspace ? t(`projects.role.${workspace.role.toLowerCase()}`) : "";

  const load = useCallback(async () => {
    if (!workspace) return;
    setLoading(true);
    setError("");
    try {
      setProjects(await api.listProjects(workspace.id, status));
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("projects.loadError"));
    } finally {
      setLoading(false);
    }
  }, [status, t, workspace]);

  // Loading remote projects after the workspace or tab changes is an external synchronization.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void load(); }, [load]);

  async function create(input: ProjectInput) {
    if (!workspace) return;
    const project = await api.createProject(workspace.id, input);
    router.push(`/projects/${project.id}`);
  }

  async function update(input: ProjectInput) {
    if (!editing) return;
    await api.updateProject(editing.id, input);
    await load();
  }

  async function archive(project: Project) {
    if (!window.confirm(t("projects.archiveConfirm"))) return;
    await api.archiveProject(project.id);
    await load();
  }

  async function restore(project: Project) {
    await api.restoreProject(project.id);
    await load();
  }

  return <div className="projects-page">
    <div className="projects-header projects-index-header">
      <div>
        <p className="section-eyebrow">{t("projects.eyebrow")}</p>
        <h1>{t("projects.title")}</h1>
        <p>{t("projects.subtitle")}</p>
      </div>
      <button className="primary-button" onClick={() => { setEditing(undefined); setFormOpen(true); }}><Plus size={16} /> {t("projects.newProject")}</button>
    </div>

    <div className="workspace-banner projects-workspace-banner">
      <span className="workspace-banner-icon"><BriefcaseBusiness size={18} /></span>
      <div><small>{workspaceTypeLabel}</small><strong>{workspace?.name}</strong></div>
      <span className="workspace-role">{workspaceRoleLabel}</span>
    </div>

    <div className="project-tabs" role="tablist" aria-label={t("projects.title")}>
      <button role="tab" aria-selected={status === "Active"} className={status === "Active" ? "is-active" : ""} onClick={() => setStatus("Active")}>{t("projects.active")} <span>{status === "Active" ? projects.length : ""}</span></button>
      <button role="tab" aria-selected={status === "Archived"} className={status === "Archived" ? "is-active" : ""} onClick={() => setStatus("Archived")}>{t("projects.archived")} <span>{status === "Archived" ? projects.length : ""}</span></button>
    </div>

    {error && <div className="inline-error" role="alert">{error}</div>}
    {loading ? <div className="loading-state"><span className="loading-spinner" /></div> : projects.length === 0 ? <div className="projects-empty"><span className="empty-icon"><FolderOpen size={24} /></span><h2>{status === "Active" ? t("projects.emptyTitle") : t("projects.emptyArchivedTitle")}</h2><p>{status === "Active" ? t("projects.emptyDescription") : t("projects.emptyArchivedDescription")}</p>{status === "Active" && <button className="primary-button" onClick={() => setFormOpen(true)}><Plus size={16} /> {t("projects.newProject")}</button>}</div> : <div className="project-list project-list-premium">{projects.map((project) => <article className={`project-card project-card-premium ${projectAccent(project.type)}`} key={project.id}>
      <Link href={`/projects/${project.id}`} className="project-card-main">
        <span className="project-card-icon"><FolderOpen size={19} /></span>
        <span className="project-card-copy"><span className="project-card-kicker">{t(typeKey(project.type))}</span><strong>{project.name}</strong><small>{project.description || t("projects.noDescription")}</small></span>
        <span className="project-card-open"><ArrowUpRight size={17} /></span>
      </Link>
      <div className="project-card-meta">
        <span><CalendarDays size={13} /> {t("projects.updated")} {formatDate(project.updatedAt, locale)}</span>
        {status === "Active" ? <div><button onClick={() => { setEditing(project); setFormOpen(true); }} aria-label={t("projects.edit")}><Pencil size={14} /></button><button onClick={() => void archive(project)} aria-label={t("projects.archive")}><Archive size={14} /></button></div> : <button onClick={() => void restore(project)} className="restore-action"><RotateCcw size={14} /> {t("projects.restore")}</button>}
      </div>
    </article>)}</div>}
    {formOpen && <ProjectForm project={editing} onClose={() => { setFormOpen(false); setEditing(undefined); }} onSubmit={editing ? update : create} />}
  </div>;
}
