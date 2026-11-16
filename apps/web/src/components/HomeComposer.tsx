"use client";
import { useRef } from "react";
import { ChevronDown, Paperclip, Send, X } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import type { Project } from "@/lib/api";

type HomeComposerProps = {
  value: string;
  onChange: (value: string) => void;
  onSubmit: () => void;
  busy: boolean;
  projects: Project[];
  projectId: string;
  onProjectChange: (projectId: string) => void;
  files: File[];
  onFilesChange: (files: File[]) => void;
  onRemoveFile: (index: number) => void;
};

function formatFileSize(bytes: number) {
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function HomeComposer({ value, onChange, onSubmit, busy, projects, projectId, onProjectChange, files, onFilesChange, onRemoveFile }: Readonly<HomeComposerProps>) {
  const { t } = useLocale();
  const fileInputRef = useRef<HTMLInputElement>(null);
  return (
    <div className="home-composer">
      <div className="home-composer-heading">
        <span className="home-composer-mark" aria-hidden="true">✦</span>
        <div><p className="home-composer-kicker">{t("home.creationKicker")}</p><h2 id="home-composer-title">{t("home.creationTitle")}</h2></div>
      </div>
      <textarea
        className="home-composer-input"
        value={value}
        onChange={(event) => onChange(event.target.value)}
        onKeyDown={(event) => { if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); onSubmit(); } }}
        placeholder={t("home.creationPlaceholder")}
        aria-label={t("home.creationPlaceholder")}
        maxLength={20000}
        disabled={busy}
        rows={4}
      />
      {files.length > 0 && <div className="home-composer-files" aria-label={t("chat.attachments")}>
        {files.map((file, index) => <span className="home-composer-file" key={`${file.name}-${file.size}-${index}`}><Paperclip size={13} /><span>{file.name}<small>{formatFileSize(file.size)}</small></span><button type="button" onClick={() => onRemoveFile(index)} aria-label={`${t("chat.removeAttachment")}: ${file.name}`} disabled={busy}><X size={13} /></button></span>)}
      </div>}
      <div className="home-composer-footer">
        <div className="home-composer-context">
          <label className="home-composer-control">
            <input ref={fileInputRef} className="home-composer-file-input visually-hidden" type="file" multiple accept=".pdf,.docx,.txt,.md,.csv,.xlsx,.jpg,.jpeg,.png,.webp" onChange={(event) => { onFilesChange(Array.from(event.target.files ?? [])); event.currentTarget.value = ""; }} disabled={busy} />
            <Paperclip size={15} /> <span>{t("home.attachAction")}</span>
          </label>
          <label className="home-composer-project">
            <span className="sr-only">{t("chat.projectSelector")}</span>
            <select value={projectId} onChange={(event) => onProjectChange(event.target.value)} disabled={busy} aria-label={t("chat.projectSelector")}>
              <option value="">{t("chat.noProject")}</option>
              {projects.map((project) => <option value={project.id} key={project.id}>{project.name}</option>)}
            </select>
            <ChevronDown size={14} aria-hidden="true" />
          </label>
        </div>
        <button type="button" className="home-composer-submit" onClick={onSubmit} disabled={busy || !value.trim()} aria-label={t("home.createAction")}>
          <span>{t("home.createAction")}</span><Send size={16} />
        </button>
      </div>
    </div>
  );
}
