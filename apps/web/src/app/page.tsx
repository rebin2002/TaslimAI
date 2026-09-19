"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { ArrowUpRight, Command, FileText, MessageSquare, Plus, Sparkles } from "lucide-react";
import { departments } from "@/lib/data";
import { useLocale } from "@/components/LocaleProvider";
import { DepartmentSection } from "@/components/DepartmentSection";
import { useAuth } from "@/components/AuthProvider";
import { api } from "@/lib/api";

export default function HomePage() {
  const { t } = useLocale();
  const { user, workspace } = useAuth();
  const router = useRouter();
  const [idea, setIdea] = useState("");
  const [asking, setAsking] = useState(false);

  async function askTaslim() {
    const prompt = idea.trim();
    if (!prompt) { router.push("/chat"); return; }
    if (!user || !workspace) { router.push("/login?next=/chat"); return; }
    setAsking(true);
    try {
      const conversation = await api.createConversation(workspace.id);
      await api.sendMessage(conversation.id, prompt);
      router.push(`/chat/${conversation.id}`);
    } finally { setAsking(false); }
  }

  return (
    <div className="home-page">
      <section className="hero-section">
        <div className="hero-copy">
          <div className="hero-kicker"><span className="pulse-dot" /> {t("home.eyebrow")}</div>
          <h1>{t("home.title")}</h1>
          <p>{t("home.subtitle")}</p>
        </div>
        <div className="hero-orb" aria-hidden="true">
          <div className="orb-ring orb-ring-one" />
          <div className="orb-ring orb-ring-two" />
          <div className="orb-core"><Sparkles size={26} /></div>
          <span className="orb-node orb-node-one" /><span className="orb-node orb-node-two" /><span className="orb-node orb-node-three" />
        </div>
      </section>

      <section className="create-panel" aria-label="Create with Taslim">
        <div className="create-panel-top">
          <span className="create-panel-icon"><Sparkles size={18} /></span>
          <input className="create-prompt-input" value={idea} onChange={(event) => setIdea(event.target.value)} onKeyDown={(event) => { if (event.key === "Enter") void askTaslim(); }} placeholder={t("home.searchPlaceholder")} aria-label={t("home.searchPlaceholder")} />
          <span className="create-panel-shortcut"><Command size={12} /> K</span>
        </div>
        <div className="create-panel-bottom">
          <div className="create-suggestions">
            <button type="button"><FileText size={14} /> Draft something</button>
            <button type="button" onClick={() => void askTaslim()} disabled={asking}><MessageSquare size={14} /> {asking ? t("home.asking") : t("home.askTaslim")}</button>
          </div>
          <button type="button" className="create-submit" onClick={() => void askTaslim()} aria-label={t("home.chatCta")} disabled={asking}><ArrowUpRight size={18} /></button>
        </div>
      </section>

      <div className="home-grid">
        <section className="workspace-card">
          <div className="workspace-card-header">
            <div><p className="section-eyebrow">{t("home.recent")}</p><h2>{t("home.emptyTitle")}</h2></div>
            <span className="workspace-card-icon"><Plus size={18} /></span>
          </div>
          <p>{t("home.emptyDescription")}</p>
          <Link href="/chat" className="text-link">{t("home.chatCta")} <ArrowUpRight size={15} /></Link>
        </section>
        <Link href="/chat" className="chat-feature-card">
          <span className="chat-feature-orb"><MessageSquare size={20} /></span>
          <span className="chat-feature-copy"><strong>{t("home.chatCta")}</strong><small>{t("home.searchPlaceholder")}</small></span>
          <ArrowUpRight size={18} />
        </Link>
      </div>

      <div id="departments" className="departments-heading"><div><p className="section-eyebrow">{t("home.explore")}</p><h2>{t("home.explore")}</h2></div><span className="department-count">06</span></div>
      <div className="departments-list">
        {departments.map((department, index) => <DepartmentSection key={department.id} department={department} index={index} />)}
      </div>
      <footer className="site-footer"><span>{t("footer.status")}</span><span>{t("footer.version")}</span></footer>
    </div>
  );
}
