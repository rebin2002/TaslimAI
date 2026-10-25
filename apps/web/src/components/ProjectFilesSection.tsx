"use client";

import { FormEvent, useEffect, useLayoutEffect, useRef, useState } from "react";
import { FileText, Paperclip, Trash2, Upload } from "lucide-react";
import { ApiError, api, type Project, type StoredFile } from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";

type ProjectFilesSectionProps = Readonly<{ project: Project }>;
export type ProjectFileUploadScope = Readonly<{ workspaceId: string; projectId: string }>;
type ProjectFileUploadGenerationRef = { current: number };
type ProjectFileUploadToken = ProjectFileUploadScope & { generation: number };

export function beginProjectFileUpload(ref: ProjectFileUploadGenerationRef, scope: ProjectFileUploadScope): ProjectFileUploadToken {
  ref.current += 1;
  return { ...scope, generation: ref.current };
}

export function isCurrentProjectFileUpload(
  token: ProjectFileUploadToken,
  ref: ProjectFileUploadGenerationRef,
  activeScope: ProjectFileUploadScope,
) {
  return token.generation === ref.current
    && token.workspaceId === activeScope.workspaceId
    && token.projectId === activeScope.projectId;
}

const MAX_FILE_SIZE = 25 * 1024 * 1024;
const ACCEPTED_EXTENSIONS = [".pdf", ".docx", ".txt", ".md", ".csv", ".xlsx", ".jpg", ".jpeg", ".png", ".webp"];

export function ProjectFilesSection({ project }: ProjectFilesSectionProps) {
  const { t } = useLocale();
  const [files, setFiles] = useState<StoredFile[]>([]);
  const [loading, setLoading] = useState(true);
  const [uploading, setUploading] = useState(false);
  const [error, setError] = useState("");
  const inputRef = useRef<HTMLInputElement>(null);
  const uploadGenerationRef = useRef(0);
  const activeProjectScopeRef = useRef<ProjectFileUploadScope>({ workspaceId: project.workspaceId, projectId: project.id });

  useLayoutEffect(() => {
    activeProjectScopeRef.current = { workspaceId: project.workspaceId, projectId: project.id };
  }, [project.id, project.workspaceId]);

  useEffect(() => {
    let active = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setLoading(true); setError(""); setUploading(false);
    void api.listFiles(project.workspaceId, project.id)
      .then(items => { if (active) setFiles(items); })
      .catch(() => { if (active) setError(t("projects.fileUploadError")); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [project.id, project.workspaceId, t]);

  async function upload(event: FormEvent<HTMLInputElement>) {
    const selected = event.currentTarget.files;
    if (!selected?.length) return;
    const uploadScope = activeProjectScopeRef.current;
    const uploadToken = beginProjectFileUpload(uploadGenerationRef, uploadScope);
    setUploading(true);
    setError("");
    try {
      for (const file of Array.from(selected)) {
        const extension = `.${file.name.split(".").pop()?.toLowerCase() ?? ""}`;
        if (!ACCEPTED_EXTENSIONS.includes(extension) || file.size <= 0 || file.size > MAX_FILE_SIZE) {
          throw new Error(t("projects.fileUploadError"));
        }
        const stored = await api.uploadFile(project.workspaceId, file, { projectId: project.id });
        if (isCurrentProjectFileUpload(uploadToken, uploadGenerationRef, activeProjectScopeRef.current)) {
          setFiles(current => [stored, ...current]);
        }
      }
    } catch (caught) {
      if (isCurrentProjectFileUpload(uploadToken, uploadGenerationRef, activeProjectScopeRef.current)) {
        setError(caught instanceof ApiError ? caught.message : t("projects.fileUploadError"));
      }
    } finally {
      if (isCurrentProjectFileUpload(uploadToken, uploadGenerationRef, activeProjectScopeRef.current)) setUploading(false);
      if (inputRef.current) inputRef.current.value = "";
    }
  }

  async function remove(file: StoredFile) {
    if (!window.confirm(t("projects.deleteFileConfirm"))) return;
    try {
      await api.deleteFile(file.id);
      setFiles(current => current.filter(item => item.id !== file.id));
    } catch {
      setError(t("projects.fileDeleteError"));
    }
  }

  return <section className="project-files-card">
    <div className="card-title project-files-heading"><div><p className="section-eyebrow">{t("projects.filesEyebrow")}</p><h2>{t("projects.filesTitle")}</h2><p>{t("projects.filesDescription")}</p></div><label className="secondary-button project-file-upload"><Upload size={15} />{uploading ? t("projects.uploadingFile") : t("projects.uploadFile")}<input ref={inputRef} type="file" className="visually-hidden" multiple accept={ACCEPTED_EXTENSIONS.join(",")} onChange={upload} disabled={uploading} /></label></div>
    {error && <p className="inline-error" role="alert">{error}</p>}
    {loading ? <div className="loading-state small"><span className="loading-spinner" /></div> : files.length === 0 ? <div className="project-files-empty"><FileText size={20} /><p>{t("projects.noFiles")}</p></div> : <div className="project-files-list">{files.map(file => <article className="project-file-row" key={file.id}><span className="project-file-icon"><Paperclip size={15} /></span><div className="project-file-copy"><strong title={file.originalFileName}>{file.originalFileName}</strong><small>{formatBytes(file.sizeBytes)} · {file.textExtractionStatus === "Ready" ? t("projects.fileReady") : file.status === "Failed" ? t("projects.fileFailed") : t("projects.fileProcessing")}</small></div><button type="button" className="icon-button danger-icon" onClick={() => void remove(file)} aria-label={`${t("projects.deleteFile")}: ${file.originalFileName}`}><Trash2 size={15} /></button></article>)}</div>}
  </section>;
}

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
