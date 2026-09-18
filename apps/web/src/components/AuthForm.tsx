"use client";

import Link from "next/link";
import { FormEvent, useState } from "react";
import { ArrowLeft, ArrowRight, Eye, EyeOff, Languages, LockKeyhole, Mail, UserRound } from "lucide-react";
import { useLocale, localeNames, locales } from "@/components/LocaleProvider";
import { useAuth } from "@/components/AuthProvider";
import { BrandMark } from "@/components/BrandMark";

export function AuthForm({ mode }: Readonly<{ mode: "login" | "register" }>) {
  const { t, locale, setLocale } = useLocale();
  const { signIn, register } = useAuth();
  const [displayName, setDisplayName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const isRegister = mode === "register";

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError("");
    if (isRegister && password !== confirmPassword) { setError(t("auth.passwordMismatch")); return; }
    setSubmitting(true);
    try {
      if (isRegister) await register({ displayName, email, password, preferredLanguage: locale });
      else await signIn({ email, password });
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("auth.genericError"));
    } finally { setSubmitting(false); }
  }

  return (
    <div className="auth-page">
      <div className="auth-backdrop"><span /><span /><span /></div>
      <div className="auth-panel">
        <div className="auth-header"><BrandMark /><label className="auth-language"><Languages size={14} /><select value={locale} onChange={(event) => setLocale(event.target.value as typeof locale)}>{locales.map((item) => <option key={item} value={item}>{localeNames[item]}</option>)}</select></label></div>
        <div className="auth-intro"><p className="section-eyebrow">{t("auth.eyebrow")}</p><h1>{isRegister ? t("auth.registerTitle") : t("auth.loginTitle")}</h1><p>{isRegister ? t("auth.registerSubtitle") : t("auth.loginSubtitle")}</p></div>
        <form className="auth-form" onSubmit={submit}>
          {isRegister && <label><span>{t("auth.displayName")}</span><div className="input-shell"><UserRound size={17} /><input required minLength={2} maxLength={120} value={displayName} onChange={(event) => setDisplayName(event.target.value)} autoComplete="name" /></div></label>}
          <label><span>{t("auth.email")}</span><div className="input-shell"><Mail size={17} /><input required type="email" value={email} onChange={(event) => setEmail(event.target.value)} autoComplete="email" /></div></label>
          <label><span>{t("auth.password")}</span><div className="input-shell"><LockKeyhole size={17} /><input required minLength={10} type={showPassword ? "text" : "password"} value={password} onChange={(event) => setPassword(event.target.value)} autoComplete={isRegister ? "new-password" : "current-password"} /><button type="button" className="password-toggle" onClick={() => setShowPassword((value) => !value)} aria-label={t("auth.showPassword")}>{showPassword ? <EyeOff size={16} /> : <Eye size={16} />}</button></div></label>
          {isRegister && <label><span>{t("auth.confirmPassword")}</span><div className="input-shell"><LockKeyhole size={17} /><input required minLength={10} type={showPassword ? "text" : "password"} value={confirmPassword} onChange={(event) => setConfirmPassword(event.target.value)} autoComplete="new-password" /></div></label>}
          {error && <div className="form-error" role="alert">{error}</div>}
          <button className="auth-submit" disabled={submitting}>{submitting ? t("auth.working") : isRegister ? t("auth.createAccount") : t("auth.signIn")} {locale === "en" ? <ArrowRight size={17} /> : <ArrowLeft size={17} />}</button>
        </form>
        <p className="auth-switch">{isRegister ? t("auth.haveAccount") : t("auth.noAccount")} <Link href={isRegister ? "/login" : "/register"}>{isRegister ? t("auth.signIn") : t("auth.createAccount")}</Link></p>
      </div>
    </div>
  );
}
