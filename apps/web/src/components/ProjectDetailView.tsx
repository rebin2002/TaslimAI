"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { ArrowLeft, Archive, CalendarDays, FileText, FolderOpen, MessageSquare, Pencil, RotateCcw, Sparkles } from "lucide-react";
import { useParams } from "next/navigation";
import { api, type Project } from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";
import { ProjectForm } from "@/components/ProjectForm";

export function ProjectDetailView() {
  const { t, locale } = useLocale();
  const params = useParams<{ projectId: string }>();
  const [project, setProject] = useState<Project | null>(null);
  const [loading, setLoading] = useState(true);
  const [formOpen, setFormOpen] = useState(false);
  const [error, setError] = useState("");
  const type = project ? t(`project.type.${project.type.toLowerCase()}`) : "";
  useEffect(() => { void api.getProject(params.projectId).then(setProject).catch((caught) => setError(caught instanceof Error ? caught.message : t("projects.loadError"))).finally(() => setLoading(false)); }, [params.projectId, t]);
  async function archive() { if (!project || !window.confirm(t("projects.archiveConfirm"))) return; const next = await api.archiveProject(project.id); setProject(next); }
  async function restore() { if (!project) return; setProject(await api.restoreProject(project.id)); }
  if (loading) return <div className="loading-state"><span className="loading-spinner" /></div>;
  if (error || !project) return <div className="inline-error">{error || t("projects.notFound")}</div>;
  return <div className="project-detail-page"><Link href="/projects" className="back-link"><ArrowLeft size={15} /> {t("projects.backToProjects")}</Link><div className="detail-header"><div><span className="detail-icon"><FolderOpen size={22} /></span><p className="section-eyebrow">{type}</p><h1>{project.name}</h1><p>{project.description || t("projects.noDescription")}</p></div><div className="detail-actions"><button className="secondary-button" onClick={() => setFormOpen(true)}><Pencil size={15} /> {t("projects.edit")}</button>{project.status === "Active" ? <button className="secondary-button" onClick={() => void archive()}><Archive size={15} /> {t("projects.archive")}</button> : <button className="primary-button" onClick={() => void restore()}><RotateCcw size={15} /> {t("projects.restore")}</button>}</div></div><div className="detail-meta"><span><CalendarDays size={15} /> {t("projects.updated")} {new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(project.updatedAt))}</span><span><Sparkles size={15} /> {t(`projects.status.${project.status.toLowerCase()}`)}</span></div><div className="future-grid"><div><MessageSquare size={20} /><strong>{t("projects.futureChat")}</strong><small>{t("projects.futurePlaceholder")}</small></div><div><FileText size={20} /><strong>{t("projects.futureFiles")}</strong><small>{t("projects.futurePlaceholder")}</small></div><div><Sparkles size={20} /><strong>{t("projects.futureAssets")}</strong><small>{t("projects.futurePlaceholder")}</small></div></div>{formOpen && <ProjectForm project={project} onClose={() => setFormOpen(false)} onSubmit={async (input) => setProject(await api.updateProject(project.id, input))} />}</div>;
}
