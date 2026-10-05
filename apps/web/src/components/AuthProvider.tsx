"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { api, type AuthResponse, type LoginInput, type OnboardingInput, type ProfileInput, type RegisterInput, type User } from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";
import { locales, type Locale } from "@/lib/i18n";
import { accountLocaleStorageKey, readStoredLocale, writeStoredLocale } from "@/lib/localePersistence";

type AuthContextValue = {
  user: User | null;
  workspace: AuthResponse["personalWorkspace"] | null;
  loading: boolean;
  signIn: (input: LoginInput) => Promise<void>;
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
  const { locale, setLocale } = useLocale();
  const router = useRouter();
  const sessionLocaleInitialized = useRef<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      const next = await api.me();
      setSession(next);
    } catch {
      setSession(null);
    } finally {
      setLoading(false);
    }
  }, []);

  // The refresh synchronizes React state with the server session after mount.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void refresh(); }, [refresh]);
  useEffect(() => {
    const user = session?.user;
    if (user?.preferredLanguage && sessionLocaleInitialized.current !== user.id) {
      sessionLocaleInitialized.current = user.id;
      let savedLocale: Locale | null = null;
      try {
        savedLocale = readStoredLocale(window.localStorage, accountLocaleStorageKey(user.id));
      } catch {
        // Access to browser storage can be blocked by privacy settings.
      }
      setLocale(savedLocale ?? user.preferredLanguage);
    }
  }, [session?.user, setLocale]);
  useEffect(() => {
    const userId = session?.user.id;
    if (!userId || sessionLocaleInitialized.current !== userId) return;
    try {
      writeStoredLocale(window.localStorage, accountLocaleStorageKey(userId), locale);
    } catch {
      // The in-memory locale remains active when browser storage is unavailable.
    }
  }, [locale, session?.user.id]);

  const signIn = useCallback(async (input: LoginInput) => {
    const next = await api.login(input);
    setSession(next);
    router.push("/projects");
  }, [router]);

  const register = useCallback(async (input: RegisterInput) => {
    const next = await api.register(input);
    setSession(next);
    router.push("/projects");
  }, [router]);

  const signOut = useCallback(async () => {
    await api.logout();
    setSession(null);
    sessionLocaleInitialized.current = null;
    router.push("/");
  }, [router]);

  const updateProfile = useCallback(async (input: ProfileInput) => {
    const next = await api.updateProfile(input);
    setSession(next);
    // The profile endpoint persists the interface language. Apply the same
    // choice immediately so Account does not require a reload to switch the
    // document language and direction.
    const nextLocale = input.preferredLanguage as Locale;
    if (locales.includes(nextLocale)) setLocale(nextLocale);
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
