import { defineConfig, devices } from "@playwright/test";

const webUrl = process.env.E2E_WEB_URL ?? "http://localhost:3000";
const serverMode = process.env.E2E_SERVER_MODE ?? "development";
if (serverMode !== "development" && serverMode !== "production") {
  throw new Error('E2E_SERVER_MODE must be "development" or "production".');
}
const serverCommand = serverMode === "production"
  ? "npm run build && npm run start -- --hostname 127.0.0.1"
  : "npm run dev -- --hostname 127.0.0.1";

export default defineConfig({
  testDir: "./e2e",
  timeout: 45_000,
  expect: { timeout: 8_000 },
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 2 : 1,
  reporter: [
    ["list"],
    ["html", { outputFolder: "playwright-report", open: "never" }],
  ],
  use: {
    baseURL: webUrl,
    locale: "en-US",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "retain-on-failure",
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
      testIgnore: /(?:mobile|rtl)[^/]*\.spec\.ts/,
    },
    {
      name: "mobile-chromium",
      use: {
        ...devices["Pixel 5"],
        viewport: { width: 390, height: 844 },
      },
      testMatch: /mobile\.spec\.ts/,
    },
    {
      name: "rtl-chromium",
      use: {
        ...devices["Desktop Chrome"],
        locale: "ar",
      },
      testMatch: /rtl[^/]*\.spec\.ts/,
    },
  ],
  webServer: process.env.E2E_WEB_URL
    ? undefined
    : {
        command: serverCommand,
        url: webUrl,
        reuseExistingServer: !process.env.CI,
        timeout: serverMode === "production" ? 300_000 : 120_000,
        env: {
          ...process.env,
          NEXT_PUBLIC_API_URL: process.env.NEXT_PUBLIC_API_URL
            ?? (serverMode === "production" ? "https://127.0.0.1:1" : "http://localhost:5000"),
        },
      },
});
