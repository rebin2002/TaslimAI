"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { api, type AuthResponse, type LoginInput, type OnboardingInput, type ProfileInput, type RegisterInput, type User } from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";
import { accountLocaleStorageKey, anonymousLocaleStorageKey, readStoredLocale } from "@/lib/localePersistence";
import { accountLanguagePackStorageKey, anonymousLanguagePackStorageKey, readStoredLanguagePack, writeStoredLanguagePack } from "@/lib/languagePackPersistence";
import { defaultLanguagePackRegistry } from "@/lib/languagePackRegistry";

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
  const { getLocaleSource, languagePackId, setLanguagePack } = useLocale();
  const router = useRouter();
  const sessionLanguagePackInitialized = useRef<string | null>(null);
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
      sessionLanguagePackInitialized.current = null;
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
      sessionLanguagePackInitialized.current = null;
      return;
    }
    if (sessionLanguagePackInitialized.current !== user.id) {
      sessionLanguagePackInitialized.current = user.id;
      let savedLanguagePack = readStoredLanguagePack(window.localStorage, accountLanguagePackStorageKey(user.id));
      if (!savedLanguagePack) savedLanguagePack = readStoredLocale(window.localStorage, accountLocaleStorageKey(user.id));
      const preferredLanguagePack = defaultLanguagePackRegistry.has(user.preferredLanguage) ? user.preferredLanguage : "en";
      const localeSource = getLocaleSource();
      if (localeSource !== "user" && (localeSource !== "anonymous-storage" || savedLanguagePack)) {
        setLanguagePack(savedLanguagePack ?? preferredLanguagePack, { persist: false, source: "account" });
      }
    }
  }, [getLocaleSource, session?.user, setLanguagePack]);
  useEffect(() => {
    const userId = session?.user.id;
    // The first-login picker is a preview until the server accepts onboarding.
    // Persisting it here would override the saved account profile after reload
    // even when completion fails or the user abandons the flow.
    if (!userId || sessionLanguagePackInitialized.current !== userId || session?.user.onboardingCompletedAt === null) return;
    try {
      // Only an explicit selector choice is local account preference.
      // Server-profile adoption or anonymous bootstrap must never overwrite it.
      if (getLocaleSource() === "user") {
        writeStoredLanguagePack(window.localStorage, accountLanguagePackStorageKey(userId), languagePackId);
      }
      window.localStorage.removeItem(anonymousLanguagePackStorageKey);
      window.localStorage.removeItem(anonymousLocaleStorageKey);
    } catch {
      // Storage can be unavailable; the selected pack remains active in memory.
    }
  }, [getLocaleSource, languagePackId, session?.user.id, session?.user.onboardingCompletedAt]);

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
    sessionLanguagePackInitialized.current = null;
    setLanguagePack("en", { persist: false, source: "default" });
    try {
      window.localStorage.removeItem(anonymousLanguagePackStorageKey);
      window.localStorage.removeItem(anonymousLocaleStorageKey);
    } catch {
      // Storage can be unavailable; signing out and resetting in-memory state still succeeds.
    }
    router.push("/");
  }, [router, setLanguagePack]);

  const updateProfile = useCallback(async (input: ProfileInput) => {
    const next = await api.updateProfile(input);
    setSession(next);
    // The profile endpoint persists the interface language. Apply the same
    // choice immediately so Account does not require a reload to switch copy,
    // document language, or direction.
    if (defaultLanguagePackRegistry.has(input.preferredLanguage)) {
      setLanguagePack(input.preferredLanguage, { persist: false, source: "account" });
      // A successful profile save is an explicit account preference.
      try {
        writeStoredLanguagePack(window.localStorage, accountLanguagePackStorageKey(next.user.id), input.preferredLanguage);
      } catch {
        // The saved server preference and in-memory choice remain authoritative.
      }
    }
  }, [setLanguagePack]);

  const completeOnboarding = useCallback(async (input: OnboardingInput) => {
    const next = await api.completeOnboarding(input);
    setSession(next);
    if (defaultLanguagePackRegistry.has(input.preferredLanguage)) {
      setLanguagePack(input.preferredLanguage, { persist: false, source: "account" });
      // First-login selection becomes durable only after onboarding succeeds.
      try {
        writeStoredLanguagePack(window.localStorage, accountLanguagePackStorageKey(next.user.id), input.preferredLanguage);
      } catch {
        // Keep the accepted server value even without browser storage.
      }
    }
  }, [setLanguagePack]);

  const value = useMemo(() => ({ user: session?.user ?? null, workspace: session?.personalWorkspace ?? null, loading, signIn, register, signOut, updateProfile, completeOnboarding, refresh }), [completeOnboarding, loading, refresh, register, session, signIn, signOut, updateProfile]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error("useAuth must be used within AuthProvider");
  return value;
}
