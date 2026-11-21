import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { NotificationBell, publishNotificationUnreadCount } from "./ActivityBell";

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: { id: "workspace-1" } }),
}));
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({
    locale: "en",
    t: (key: string, variables?: Record<string, string>) => Object.entries(variables ?? {}).reduce((value, [name, replacement]) => value.replace(`{${name}}`, replacement), key),
  }),
}));
vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: vi.fn() }),
}));
vi.mock("@/lib/api", () => ({
  api: {
    listNotifications: vi.fn(),
    getNotificationUnreadCount: vi.fn(),
    markNotificationRead: vi.fn(),
    markAllNotificationsRead: vi.fn(),
  },
}));

describe("NotificationBell badge contract", () => {
  it("surfaces the live workspace unread count while no panel session owns the badge", () => {
    const html = renderToStaticMarkup(<NotificationBell unreadCount={7} />);
    expect(html).toContain("notification-badge");
    expect(html).toContain(">7<");
    expect(html).toContain("navigation.notifications, notification.unread");
  });

  it("caps the badge at 99+ before the number escapes the pill", () => {
    const html = renderToStaticMarkup(<NotificationBell unreadCount={150} />);
    expect(html).toContain("99+");
  });

  it("hides the badge and the unread suffix once everything is read", () => {
    const html = renderToStaticMarkup(<NotificationBell unreadCount={0} />);
    expect(html).not.toContain("notification-badge");
    expect(html).not.toContain("notification.unread");
    expect(html).toContain("navigation.notifications");
    expect(html).toContain('aria-expanded="false"');
  });

  it("keeps unread counts behind the panel and never renders notification internals in the closed trigger", () => {
    const html = renderToStaticMarkup(<NotificationBell unreadCount={2} />);
    expect(html).not.toContain("notification-panel-list");
    expect(html).not.toContain('role="dialog"');
    expect(html).not.toContain("notification-row");
    expect(html).not.toContain("workspace-1");
    expect(html).not.toContain("resourceTitle");
  });

  it("is a safe no-op when publishing counts during server rendering", () => {
    expect(() => publishNotificationUnreadCount("workspace-1", 4)).not.toThrow();
  });
});
