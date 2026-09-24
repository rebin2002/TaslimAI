import type { Metadata } from "next";
import { AppShell } from "@/components/AppShell";
import { AuthProvider } from "@/components/AuthProvider";
import { LocaleProvider } from "@/components/LocaleProvider";
import { OnboardingGate } from "@/components/OnboardingGate";
import "./globals.css";

export const metadata: Metadata = {
  title: "Taslim.ai — Make more possible",
  description: "A calm, connected workspace for creating with AI.",
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en" dir="ltr">
      <body>
        <LocaleProvider>
          <AuthProvider>
            <AppShell>{children}</AppShell>
            <OnboardingGate />
          </AuthProvider>
        </LocaleProvider>
      </body>
    </html>
  );
}
