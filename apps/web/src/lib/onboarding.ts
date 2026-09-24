export const onboardingWorkflowDefinitions = [
  { intent: "project", href: "/projects?create=1", titleKey: "onboarding.workflow.project.title", descriptionKey: "onboarding.workflow.project.description", icon: "project" },
  { intent: "chat", href: "/chat", titleKey: "onboarding.workflow.chat.title", descriptionKey: "onboarding.workflow.chat.description", icon: "chat" },
  { intent: "image", href: "/create/image", titleKey: "onboarding.workflow.image.title", descriptionKey: "onboarding.workflow.image.description", icon: "image" },
  { intent: "document", href: "/create/document", titleKey: "onboarding.workflow.document.title", descriptionKey: "onboarding.workflow.document.description", icon: "document" },
  { intent: "presentation", href: "/create/presentation", titleKey: "onboarding.workflow.presentation.title", descriptionKey: "onboarding.workflow.presentation.description", icon: "presentation" },
  { intent: "research", href: "/create/research", titleKey: "onboarding.workflow.research.title", descriptionKey: "onboarding.workflow.research.description", icon: "research" },
] as const;

export type OnboardingIntent = (typeof onboardingWorkflowDefinitions)[number]["intent"];

export function onboardingWorkflowFor(intent: OnboardingIntent) {
  return onboardingWorkflowDefinitions.find((workflow) => workflow.intent === intent)!;
}

export function shouldShowOnboarding(user: { onboardingCompletedAt: string | null } | null) {
  return user !== null && user.onboardingCompletedAt === null;
}
