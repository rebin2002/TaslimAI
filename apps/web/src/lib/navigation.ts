import type { LucideIcon } from "lucide-react";
import {
  Activity,
  FileText,
  Image,
  LayoutDashboard,
  MessageSquare,
  Music2,
  Presentation,
  Search,
  Sparkles,
  UserRound,
  Video,
  Volume2,
} from "lucide-react";

export type NavigationItem = {
  href: string;
  labelKey: string;
  icon: LucideIcon;
};

export type StudioItem = NavigationItem & {
  descriptionKey: string;
};

export type StudioCategory = {
  id: string;
  labelKey: string;
  studios: StudioItem[];
};

export const primaryNavigation: NavigationItem[] = [
  { href: "/", labelKey: "navigation.home", icon: LayoutDashboard },
  { href: "/projects", labelKey: "navigation.projects", icon: FileText },
  { href: "/create", labelKey: "navigation.create", icon: Sparkles },
  { href: "/notifications", labelKey: "navigation.activity", icon: Activity },
  { href: "/account", labelKey: "navigation.account", icon: UserRound },
];

export const desktopNavigation: NavigationItem[] = [
  primaryNavigation[0],
  { href: "/chat", labelKey: "navigation.chat", icon: MessageSquare },
  primaryNavigation[1],
  { href: "/assets", labelKey: "navigation.assets", icon: FileText },
  primaryNavigation[2],
  primaryNavigation[3],
];

export const studioCategories: StudioCategory[] = [
  {
    id: "media",
    labelKey: "create.category.media",
    studios: [
      { href: "/create/image", labelKey: "navigation.imageStudio", descriptionKey: "create.studio.imageHint", icon: Image },
      { href: "/create/movie", labelKey: "navigation.movieStudio", descriptionKey: "create.studio.movieHint", icon: Video },
      { href: "/create/voice", labelKey: "navigation.voiceStudio", descriptionKey: "create.studio.voiceHint", icon: Volume2 },
      { href: "/create/music", labelKey: "navigation.musicStudio", descriptionKey: "create.studio.musicHint", icon: Music2 },
    ],
  },
  {
    id: "work",
    labelKey: "create.category.work",
    studios: [
      { href: "/create/document", labelKey: "navigation.documentStudio", descriptionKey: "create.studio.documentHint", icon: FileText },
      { href: "/create/presentation", labelKey: "navigation.presentationStudio", descriptionKey: "create.studio.presentationHint", icon: Presentation },
      { href: "/create/research", labelKey: "navigation.researchStudio", descriptionKey: "create.studio.researchHint", icon: Search },
    ],
  },
  {
    id: "marketing",
    labelKey: "create.category.marketing",
    studios: [
      { href: "/create/social", labelKey: "navigation.socialStudio", descriptionKey: "create.studio.socialHint", icon: MessageSquare },
    ],
  },
];

export const studioRoutes = studioCategories.flatMap((category) => category.studios.map((studio) => studio.href));

export function matchesNavigationPath(pathname: string, href: string): boolean {
  return href === "/" ? pathname === "/" : pathname === href || pathname.startsWith(`${href}/`);
}
