import { readFile } from "node:fs/promises";
import type { GenerationJob } from "../src/lib/api";
import { apiJson, E2E_API_URL, expect, registerInUi, test } from "./fixtures";

const imageJobId = "e2e-image-success-job";
const assetId = "00000000-0000-4000-8000-000000000123";
const prompt = "A small blue paper boat on a calm lake at sunrise";
const privateProviderMarker = "PRIVATE_PROVIDER_DIAGNOSTIC_MUST_NOT_RENDER";
const pngFixture = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=",
  "base64",
);

function isLoopbackHttpUrl(value: string | undefined) {
  try {
    const url = new URL(value ?? "");
    return url.protocol === "http:" && ["localhost", "127.0.0.1", "::1", "[::1]"].includes(url.hostname);
  } catch {
    return false;
  }
}

const safeTestTargetsOnly =
  isLoopbackHttpUrl(E2E_API_URL) &&
  isLoopbackHttpUrl(process.env.E2E_WEB_URL ?? "http://localhost:3000") &&
  (!process.env.NEXT_PUBLIC_API_URL || isLoopbackHttpUrl(process.env.NEXT_PUBLIC_API_URL));

function imageJob(workspaceId: string, status: GenerationJob["status"], resultJson: string | null = null): GenerationJob {
  const now = "2026-10-30T00:00:00.000Z";
  const succeeded = status === "Succeeded";
  return {
    id: imageJobId,
    workspaceId,
    projectId: null,
    jobType: "image.generate",
    status,
    title: "E2E Image Preview Fixture",
    progressPercent: succeeded ? 100 : 25,
    resultJson,
    errorCode: null,
    errorMessage: null,
    cancellationRequested: false,
    createdAt: now,
    queuedAt: now,
    startedAt: now,
    completedAt: succeeded ? now : null,
    failedAt: null,
    cancelledAt: null,
    outputs: [],
  };
}

test("authenticated Image Studio generates, previews, and downloads a local fixture", async ({ page, testUser }) => {
  test.skip(!safeTestTargetsOnly, "Runs only against loopback HTTP web/API targets; it must never register against a remote service.");

  const blockedExternalRequests: string[] = [];
  await page.route("**/*", async (route) => {
    if (!isLoopbackHttpUrl(route.request().url())) {
      blockedExternalRequests.push(`${route.request().method()} ${route.request().url()}`);
      await route.abort("blockedbyclient");
      return;
    }
    await route.fallback();
  });

  let registrationAttempted = false;
  try {
    // Use the canonical real UI registration helper, but only after the loopback guard is active.
    registrationAttempted = true;
    await registerInUi(page, testUser);
    const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
    expect(auth.personalWorkspace.id).toBeTruthy();

    const apiOrigin = new URL(E2E_API_URL).origin;
    const webOrigin = new URL(process.env.E2E_WEB_URL ?? "http://localhost:3000").origin;
    const corsHeaders = {
      "Access-Control-Allow-Origin": webOrigin,
      "Access-Control-Allow-Credentials": "true",
      "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
      "Access-Control-Allow-Headers": "content-type,x-csrf-token,idempotency-key",
      Vary: "Origin",
    };

    let createRequests = 0;
    let pollRequests = 0;
    let previewRequests = 0;
    let downloadRequests = 0;
    let createdInput: Record<string, unknown> | null = null;
    const previewOrigins: string[] = [];
    const downloadOrigins: string[] = [];

    await page.route((url) => url.origin === apiOrigin && url.pathname === "/api/image-generation/jobs", async (route) => {
      const method = route.request().method();
      if (method === "OPTIONS") {
        await route.fulfill({ status: 204, headers: corsHeaders });
        return;
      }
      if (method !== "POST") {
        await route.fallback();
        return;
      }
      createRequests += 1;
      const headers = route.request().headers();
      expect(headers["x-csrf-token"]).toBeTruthy();
      createdInput = route.request().postDataJSON() as Record<string, unknown>;
      expect(createdInput).toMatchObject({
        workspaceId: auth.personalWorkspace.id,
        projectId: null,
        description: prompt,
      });
      expect(createdInput).not.toHaveProperty("provider");
      expect(createdInput).not.toHaveProperty("apiKey");
      await route.fulfill({
        status: 200,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
        body: JSON.stringify({ job: imageJob(auth.personalWorkspace.id, "Running") }),
      });
    });

    await page.route((url) => url.origin === apiOrigin && url.pathname === `/api/generation/jobs/${imageJobId}`, async (route) => {
      if (route.request().method() !== "GET") {
        await route.fallback();
        return;
      }
      pollRequests += 1;
      await route.fulfill({
        status: 200,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
        body: JSON.stringify({
          ...imageJob(
            auth.personalWorkspace.id,
            "Succeeded",
            JSON.stringify({
              assetId,
              assetType: "image",
              format: "png",
              width: 1,
              height: 1,
              aspectRatio: "square",
              quality: "standard",
              provider: privateProviderMarker,
            }),
          ),
        }),
      });
    });

    // Production sets API_URL to an empty string, so assetFileUrl is a relative same-origin
    // /api URL. CI runs Next in development against an HTTP API origin that the page CSP
    // intentionally excludes from img-src. After real signup, mirror only that production
    // relative-URL behavior for synthetic asset img/src and download-link href attributes.
    // No document response is intercepted and no CSP/auth policy is modified.
    await page.addInitScript((proxyTarget: { origin: string; assetPath: string }) => {
      const relativeAssetUrl = (value: string) => {
        try {
          const parsed = new URL(value, window.location.href);
          if (parsed.origin === proxyTarget.origin && parsed.pathname === proxyTarget.assetPath) {
            return `${parsed.pathname}${parsed.search}${parsed.hash}`;
          }
        } catch {
          // Leave non-URL and non-asset values untouched.
        }
        return value;
      };

      const nativeSetAttribute = Element.prototype.setAttribute;
      Element.prototype.setAttribute = function (name: string, value: string) {
        const lowerName = name.toLowerCase();
        if ((this instanceof HTMLImageElement && lowerName === "src") || (this instanceof HTMLAnchorElement && lowerName === "href")) {
          value = relativeAssetUrl(value);
        }
        return nativeSetAttribute.call(this, name, value);
      };

      const imageSrc = Object.getOwnPropertyDescriptor(HTMLImageElement.prototype, "src");
      if (imageSrc?.get && imageSrc.set) {
        Object.defineProperty(HTMLImageElement.prototype, "src", {
          configurable: imageSrc.configurable,
          enumerable: imageSrc.enumerable,
          get: imageSrc.get,
          set(this: HTMLImageElement, value: string) {
            imageSrc.set!.call(this, relativeAssetUrl(value));
          },
        });
      }

      const anchorHref = Object.getOwnPropertyDescriptor(HTMLAnchorElement.prototype, "href");
      if (anchorHref?.get && anchorHref.set) {
        Object.defineProperty(HTMLAnchorElement.prototype, "href", {
          configurable: anchorHref.configurable,
          enumerable: anchorHref.enumerable,
          get: anchorHref.get,
          set(this: HTMLAnchorElement, value: string) {
            anchorHref.set!.call(this, relativeAssetUrl(value));
          },
        });
      }
    }, { origin: apiOrigin, assetPath: `/api/assets/${assetId}/download` });

    await page.route((url) => url.origin === webOrigin && url.pathname === `/api/assets/${assetId}/download`, async (route) => {
      const requestUrl = new URL(route.request().url());
      if (requestUrl.searchParams.get("inline") === "true") {
        previewRequests += 1;
        previewOrigins.push(requestUrl.origin);
        await route.fulfill({
          status: 200,
          headers: { "Content-Type": "image/png", "Cache-Control": "no-store" },
          body: pngFixture,
        });
        return;
      }

      downloadRequests += 1;
      downloadOrigins.push(requestUrl.origin);
      await route.fulfill({
        status: 200,
        headers: {
          "Content-Type": "image/png",
          "Content-Disposition": 'attachment; filename="taslim-image-e2e.png"',
          "Cache-Control": "no-store",
        },
        body: pngFixture,
      });
    });

    await page.goto("/create");
    await page.locator('a[href="/create/image"]').click();
    await expect(page).toHaveURL(/\/create\/image$/);
    const projectSelect = page.locator(".image-project-select select");
    await expect(projectSelect).toBeEnabled({ timeout: 15_000 });
    const promptField = page.locator(".image-prompt-field textarea");
    await expect(promptField).toBeVisible();
    await promptField.fill(prompt);
    const generateButton = page.getByRole("button", { name: /^Generate image/ });
    await expect(generateButton).toBeVisible();
    await expect(generateButton).toBeEnabled();
    await generateButton.click();

    const successCanvas = page.locator(".image-success-canvas");
    await expect(successCanvas).toBeVisible({ timeout: 20_000 });
    await expect(successCanvas).toContainText("Ready to use");
    const preview = page.getByRole("img", { name: prompt });
    await expect(preview).toBeVisible();
    await expect.poll(() => preview.evaluate((image: HTMLImageElement) => image.naturalWidth), { timeout: 10_000 }).toBe(1);
    await expect(preview).toHaveAttribute("src", `/api/assets/${assetId}/download?inline=true`);

    const downloadLink = successCanvas.getByRole("link", { name: "Download", exact: true });
    await expect(downloadLink).toHaveAttribute("href", `/api/assets/${assetId}/download`);
    const downloadPromise = page.waitForEvent("download");
    await downloadLink.click();
    const download = await downloadPromise;
    expect(download.suggestedFilename()).toBe("taslim-image-e2e.png");
    const downloadPath = await download.path();
    expect(downloadPath).toBeTruthy();
    expect(await readFile(downloadPath!)).toEqual(pngFixture);

    expect(createRequests).toBe(1);
    expect(pollRequests).toBeGreaterThan(0);
    expect(createdInput).not.toBeNull();
    expect(previewRequests).toBeGreaterThan(0);
    expect(downloadRequests).toBe(1);
    expect(previewOrigins.every((origin) => origin === webOrigin)).toBe(true);
    expect(downloadOrigins).toEqual([webOrigin]);
    await expect(page.locator("body")).not.toContainText(privateProviderMarker);
    expect(blockedExternalRequests).toEqual([]);
  } finally {
    if (registrationAttempted) {
      await apiJson(page.request, "POST", "/api/auth/logout").catch(() => undefined);
    }
  }
});
