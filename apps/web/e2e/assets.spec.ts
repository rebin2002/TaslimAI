import { test, expect, apiJson } from "./fixtures";

type AuthResponse = {
  personalWorkspace: { id: string };
};

type GenerationJob = {
  id: string;
  status: string;
};

type Asset = {
  id: string;
  name: string;
  description: string | null;
  status: "Active" | "Archived";
  hasFile: boolean;
};

type AssetList = {
  items: Asset[];
};

test.describe("Asset Library lifecycle", () => {
  test("publishes, downloads, edits, archives, and restores a deterministic generated asset", async ({ authenticatedPage: page }) => {
    const auth = await apiJson<AuthResponse>(page.request, "GET", "/api/auth/me");
    const title = `E2E Asset Library ${Date.now().toString(36)}`;
    const renamedTitle = `${title} renamed`;

    const created = await apiJson<GenerationJob>(page.request, "POST", "/api/generation/jobs", {
      workspaceId: auth.personalWorkspace.id,
      jobType: "system.test",
      title,
      inputJson: JSON.stringify({ purpose: "asset-library-regression" }),
    });

    await expect.poll(
      async () => (await apiJson<GenerationJob>(page.request, "GET", `/api/generation/jobs/${created.id}`)).status,
      { timeout: 30_000, intervals: [100, 250, 500, 1_000] },
    ).toBe("Succeeded");

    await expect.poll(
      async () => {
        const assets = await apiJson<AssetList>(
          page.request,
          "GET",
          `/api/assets?workspaceId=${auth.personalWorkspace.id}&status=Active&page=1&pageSize=12`,
        );
        return assets.items.some((asset) => asset.name === title && asset.hasFile);
      },
      { timeout: 10_000, intervals: [100, 250, 500, 1_000] },
    ).toBe(true);

    const assets = await apiJson<AssetList>(
      page.request,
      "GET",
      `/api/assets?workspaceId=${auth.personalWorkspace.id}&status=Active&page=1&pageSize=12`,
    );
    const asset = assets.items.find((item) => item.name === title);
    expect(asset).toBeDefined();
    expect(asset?.hasFile).toBe(true);

    await page.goto("/assets");
    await expect(page.getByRole("heading", { name: /^assets$/i })).toBeVisible();
    const card = page.locator("article.asset-card").filter({ hasText: title });
    await expect(card).toBeVisible();
    await expect(card.getByRole("link", { name: /download/i })).toHaveAttribute(
      "href",
      new RegExp(`/api/assets/${asset!.id}/download$`),
    );

    const downloadPromise = page.waitForEvent("download");
    await card.getByRole("link", { name: /download/i }).click();
    const download = await downloadPromise;
    expect(download.suggestedFilename()).toBe("generation-result.json");

    await card.getByRole("button", { name: /^edit$/i }).click();
    const editForm = page.locator("form.asset-edit-modal");
    await expect(editForm).toBeVisible();
    await editForm.getByLabel("Asset name").fill(renamedTitle);
    await editForm.getByLabel("Description").fill("Renamed by the deterministic Asset Library E2E flow.");
    await editForm.getByRole("button", { name: /save changes/i }).click();
    await expect(editForm).toBeHidden();

    const renamedCard = page.locator("article.asset-card").filter({ hasText: renamedTitle });
    await expect(renamedCard).toBeVisible();
    const renamed = await apiJson<Asset>(page.request, "GET", `/api/assets/${asset!.id}`);
    expect(renamed.name).toBe(renamedTitle);
    expect(renamed.description).toBe("Renamed by the deterministic Asset Library E2E flow.");
    expect(renamed.status).toBe("Active");

    page.once("dialog", (dialog) => void dialog.accept());
    await renamedCard.getByRole("button", { name: /^archive$/i }).click();
    await expect(renamedCard).toHaveCount(0);
    expect((await apiJson<Asset>(page.request, "GET", `/api/assets/${asset!.id}`)).status).toBe("Archived");

    await page.getByRole("button", { name: "Archived", exact: true }).click();
    const archivedCard = page.locator("article.asset-card").filter({ hasText: renamedTitle });
    await expect(archivedCard).toBeVisible();
    await archivedCard.getByRole("button", { name: /^restore$/i }).click();
    await expect(archivedCard).toHaveCount(0);
    expect((await apiJson<Asset>(page.request, "GET", `/api/assets/${asset!.id}`)).status).toBe("Active");

    await page.getByRole("button", { name: "Active", exact: true }).click();
    await expect(page.locator("article.asset-card").filter({ hasText: renamedTitle })).toBeVisible();
    await expect(page.locator("article.asset-card").filter({ hasText: renamedTitle }).getByRole("link", { name: /download/i })).toHaveAttribute(
      "href",
      new RegExp(`/api/assets/${asset!.id}/download$`),
    );
  });
});
