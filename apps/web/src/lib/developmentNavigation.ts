import { departments, featureRoute, type Feature } from "@/lib/data";

export type DevelopmentNavigationItem = Pick<Feature, "id" | "labelKey" | "icon" | "tone"> & {
  href: string;
};

const developmentDepartment = departments.find((department) => department.id === "development");

if (!developmentDepartment) {
  throw new Error("The development department navigation is not configured");
}

export const developmentNavigation: DevelopmentNavigationItem[] = developmentDepartment.features.map((feature) => ({
  id: feature.id,
  labelKey: feature.labelKey,
  icon: feature.icon,
  tone: feature.tone,
  href: featureRoute("development", feature.id),
}));
