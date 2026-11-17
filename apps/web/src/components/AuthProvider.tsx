"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { api, type AuthResponse, type LoginInput, type OnboardingInput, type ProfileInput, type RegisterInput, type User } from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";
import { locales, type Locale } from "@/lib/i18n";
import { accountLocaleStorageKey, anonymousLocaleStorageKey, readStoredLocale, writeStoredLocale } from "@/lib/localePersistence";

type AuthContextValue = {
  user: User | null;
  workspace: AuthResponse["personalWorkspace"] | null;
  loading: boolean;
  signIn: (input: LoginInput, redirectTo?: string) => Promise<void>;
  register: (input: RegisterInput) => Promise<void>;
  signOut: () => Promise<void>;
  updateProfile: (input: ProfileInput) => Promise<void>;
  completeOnboarding: (input: OnboardingInput) => Promise<void>;
  refresh: () => Promise<void>;
};

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: Readonly<{ children: React.ReactNode }>) {
  const [session, setSession] = useState<AuthResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const { getLocaleSource, locale, setLocale } = useLocale();
  const router = useRouter();
  const sessionLocaleInitialized = useRef<string | null>(null);
  const authRequestGeneration = useRef(0);

  const refresh = useCallback(async () => {
    const request = ++authRequestGeneration.current;
    try {
      const next = await api.me();
      if (request !== authRequestGeneration.current) return;
      setSession(next);
    } catch {
      if (request !== authRequestGeneration.current) return;
      setSession(null);
      sessionLocaleInitialized.current = null;
    } finally {
      if (request === authRequestGeneration.current) setLoading(false);
    }
  }, []);

  // The refresh synchronizes React state with the server session after mount.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void refresh(); }, [refresh]);
  useEffect(() => {
    const user = session?.user;
    if (!user) {
      sessionLocaleInitialized.current = null;
      return;
    }
    if (sessionLocaleInitialized.current !== user.id) {
      sessionLocaleInitialized.current = user.id;
      let savedLocale: Locale | null = null;
      try {
        savedLocale = readStoredLocale(window.localStorage, accountLocaleStorageKey(user.id));
      } catch {
        // Access to browser storage can be blocked by privacy settings.
      }
      const localeSource = getLocaleSource();
      // An explicit in-session selector choice keeps the floor. The
      // mount-time anonymous bootstrap does too, but only while the account
      // has no saved selector choice of its own: declining adoption here
      // while the persistence effect below writes the current locale would
      // overwrite that saved choice with the stale bootstrap value and lock
      // it in for every later session on this browser.
      if (localeSource !== "user" && (localeSource !== "anonymous-storage" || savedLocale)) {
        setLocale(savedLocale ?? user.preferredLanguage, { persist: false, source: "account" });
      }
    }
  }, [getLocaleSource, session?.user, setLocale]);
  useEffect(() => {
    const userId = session?.user.id;
    if (!userId || sessionLocaleInitialized.current !== userId) return;
    try {
      // The account key stores only explicit choices - the header selector
      // (source "user") or a profile save (persisted in updateProfile). An
      // automatically adopted server profile language or a restored device
      // bootstrap must not masquerade as one, or it would outvote the user's
      // real saved selector choice on the next sign-in.
      if (getLocaleSource() === "user") {
        writeStoredLocale(window.localStorage, accountLocaleStorageKey(userId), locale);
      }
      window.localStorage.removeItem(anonymousLocaleStorageKey);
    } catch {
      // The in-memory locale remains active when browser storage is unavailable.
    }
  }, [getLocaleSource, locale, session?.user.id]);

  const signIn = useCallback(async (input: LoginInput, redirectTo?: string) => {
    const request = ++authRequestGeneration.current;
    const next = await api.login(input);
    if (request !== authRequestGeneration.current) return;
    setSession(next);
    if (redirectTo) router.replace(redirectTo);
    else router.push("/projects");
  }, [router]);

  const register = useCallback(async (input: RegisterInput) => {
    const request = ++authRequestGeneration.current;
    const next = await api.register(input);
    if (request !== authRequestGeneration.current) return;
    setSession(next);
    router.push("/projects");
  }, [router]);

  const signOut = useCallback(async () => {
    const request = ++authRequestGeneration.current;
    await api.logout();
    if (request !== authRequestGeneration.current) return;
    setSession(null);
    sessionLocaleInitialized.current = null;
    setLocale("en", { persist: false, source: "default" });
    try { window.localStorage.removeItem(anonymousLocaleStorageKey); } catch { /* Storage can be unavailable. */ }
    router.push("/");
  }, [router, setLocale]);

  const updateProfile = useCallback(async (input: ProfileInput) => {
    const next = await api.updateProfile(input);
    setSession(next);
    // The profile endpoint persists the interface language. Apply the same
    // choice immediately so Account does not require a reload to switch the
    // document language and direction.
    const nextLocale = input.preferredLanguage as Locale;
    if (locales.includes(nextLocale)) {
      setLocale(nextLocale, { persist: false, source: "account" });
      // A profile save is an explicit choice, so it belongs in the account
      // key right away even though the shared persistence effect only
      // follows selector actions.
      try {
        writeStoredLocale(window.localStorage, accountLocaleStorageKey(next.user.id), nextLocale);
      } catch {
        // The server profile remains the source of truth when browser
        // storage is unavailable; the in-memory choice still applies.
      }
    }
  }, [setLocale]);

  const completeOnboarding = useCallback(async (input: OnboardingInput) => {
    const next = await api.completeOnboarding(input);
    setSession(next);
  }, []);

  const value = useMemo(() => ({ user: session?.user ?? null, workspace: session?.personalWorkspace ?? null, loading, signIn, register, signOut, updateProfile, completeOnboarding, refresh }), [completeOnboarding, loading, refresh, register, session, signIn, signOut, updateProfile]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error("useAuth must be used within AuthProvider");
  return value;
}
