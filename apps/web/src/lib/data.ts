import type { LucideIcon } from "lucide-react";
import {
  Activity,
  BarChart3,
  BriefcaseBusiness,
  Bug,
  Building2,
  CalendarDays,
  Code2,
  Compass,
  FileText,
  GraduationCap,
  Image,
  LayoutDashboard,
  LibraryBig,
  Megaphone,
  MessageSquare,
  Music2,
  Palette,
  Plane,
  Presentation,
  Rocket,
  Search,
  ShieldCheck,
  ShoppingBag,
  Sparkles,
  UserRound,
  Users,
  Video,
  Volume2,
  WalletCards,
  Wrench,
} from "lucide-react";

export type TranslationKey = string;

export type Department = {
  id: string;
  titleKey: TranslationKey;
  eyebrowKey: TranslationKey;
  icon: LucideIcon;
  color: string;
  features: Feature[];
};

export type Feature = {
  id: string;
  labelKey: TranslationKey;
  icon: LucideIcon;
  tone: string;
};

export const navigation = [
  { href: "/", labelKey: "navigation.home", icon: LayoutDashboard },
  { href: "/chat", labelKey: "navigation.chat", icon: MessageSquare },
  { href: "/projects", labelKey: "navigation.projects", icon: BriefcaseBusiness },
  { href: "/assets", labelKey: "navigation.assets", icon: LibraryBig },
  { href: "/create/document", labelKey: "navigation.documentStudio", icon: FileText },
  { href: "/notifications", labelKey: "navigation.notifications", icon: Activity },
  { href: "/account", labelKey: "navigation.account", icon: UserRound },
] as const;

export const departments: Department[] = [
  {
    id: "media",
    titleKey: "department.media",
    eyebrowKey: "department.mediaEyebrow",
    icon: Palette,
    color: "coral",
    features: [
      { id: "images", labelKey: "feature.images", icon: Image, tone: "amber" },
      { id: "movies", labelKey: "feature.movies", icon: Video, tone: "violet" },
      { id: "voice", labelKey: "feature.voice", icon: Volume2, tone: "blue" },
      { id: "music", labelKey: "feature.music", icon: Music2, tone: "teal" },
    ],
  },
  {
    id: "business",
    titleKey: "department.business",
    eyebrowKey: "department.businessEyebrow",
    icon: BriefcaseBusiness,
    color: "blue",
    features: [
      { id: "company", labelKey: "feature.company", icon: Building2, tone: "blue" },
      { id: "sales", labelKey: "feature.sales", icon: BarChart3, tone: "green" },
      { id: "finance", labelKey: "feature.finance", icon: WalletCards, tone: "amber" },
      { id: "planning", labelKey: "feature.planning", icon: CalendarDays, tone: "violet" },
      { id: "research", labelKey: "feature.research", icon: Search, tone: "teal" },
      { id: "hr", labelKey: "feature.hr", icon: Users, tone: "coral" },
    ],
  },
  {
    id: "marketing",
    titleKey: "department.marketing",
    eyebrowKey: "department.marketingEyebrow",
    icon: Megaphone,
    color: "teal",
    features: [
      { id: "social-media", labelKey: "feature.socialMedia", icon: MessageSquare, tone: "teal" },
      { id: "advertising", labelKey: "feature.advertising", icon: Megaphone, tone: "coral" },
      { id: "branding", labelKey: "feature.branding", icon: Sparkles, tone: "violet" },
      { id: "content", labelKey: "feature.content", icon: FileText, tone: "blue" },
      { id: "design", labelKey: "feature.design", icon: Palette, tone: "amber" },
      { id: "product-catalog", labelKey: "feature.productCatalog", icon: ShoppingBag, tone: "green" },
      { id: "campaigns", labelKey: "feature.campaigns", icon: Rocket, tone: "coral" },
    ],
  },
  {
    id: "personal",
    titleKey: "department.personal",
    eyebrowKey: "department.personalEyebrow",
    icon: Compass,
    color: "amber",
    features: [
      { id: "photos", labelKey: "feature.photos", icon: Image, tone: "coral" },
      { id: "documents", labelKey: "feature.documents", icon: FileText, tone: "blue" },
      { id: "learning", labelKey: "feature.learning", icon: GraduationCap, tone: "violet" },
      { id: "health", labelKey: "feature.health", icon: ShieldCheck, tone: "green" },
      { id: "travel", labelKey: "feature.travel", icon: Plane, tone: "teal" },
      { id: "lifestyle", labelKey: "feature.lifestyle", icon: Sparkles, tone: "amber" },
    ],
  },
  {
    id: "education",
    titleKey: "department.education",
    eyebrowKey: "department.educationEyebrow",
    icon: GraduationCap,
    color: "violet",
    features: [
      { id: "study", labelKey: "feature.study", icon: Search, tone: "blue" },
      { id: "teaching", labelKey: "feature.teaching", icon: Presentation, tone: "coral" },
      { id: "research", labelKey: "feature.research", icon: FileText, tone: "teal" },
      { id: "courses", labelKey: "feature.courses", icon: GraduationCap, tone: "violet" },
    ],
  },
  {
    id: "development",
    titleKey: "department.development",
    eyebrowKey: "department.developmentEyebrow",
    icon: Code2,
    color: "green",
    features: [
      { id: "learn", labelKey: "feature.learn", icon: GraduationCap, tone: "blue" },
      { id: "build", labelKey: "feature.build", icon: Wrench, tone: "amber" },
      { id: "code", labelKey: "feature.code", icon: Code2, tone: "teal" },
      { id: "debug", labelKey: "feature.debug", icon: Bug, tone: "coral" },
      { id: "projects", labelKey: "feature.projects", icon: BriefcaseBusiness, tone: "violet" },
    ],
  },
];

export const routeLabels: Record<string, string> = {
  "/chat": "navigation.chat",
  "/projects": "navigation.projects",
  "/assets": "navigation.assets",
  "/create/document": "navigation.documentStudio",
  "/notifications": "navigation.notifications",
  "/account": "navigation.account",
};

export const featureRoute = (departmentId: string, featureId: string) =>
  departmentId === "media" && featureId === "images"
    ? "/create/image"
    : departmentId === "personal" && featureId === "documents"
      ? "/create/document"
    : `/${departmentId === "media" ? "media" : departmentId}/${featureId}`;
