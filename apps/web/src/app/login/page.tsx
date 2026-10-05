import { AuthForm } from "@/components/AuthForm";

type LoginPageProps = {
  searchParams: Promise<{ next?: string | string[] }>;
};

export default async function LoginPage({ searchParams }: LoginPageProps) {
  const params = await searchParams;
  const nextPath = typeof params.next === "string" ? params.next : null;
  return <AuthForm mode="login" nextPath={nextPath} />;
}
