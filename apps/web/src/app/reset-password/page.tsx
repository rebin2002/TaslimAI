import type { Metadata } from "next";
import { PasswordRecoveryForm } from "@/components/PasswordRecoveryForm";

export const metadata: Metadata = {
  title: "Choose a new Taslim.ai password",
  robots: { index: false, follow: false },
  referrer: "no-referrer",
};

export const dynamic = "force-dynamic";

export default function ResetPasswordPage() {
  return <PasswordRecoveryForm mode="reset" />;
}
