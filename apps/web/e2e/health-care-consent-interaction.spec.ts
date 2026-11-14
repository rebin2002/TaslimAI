import { test, expect } from "./fixtures";

test.describe("Health Care synthetic consent journey — authenticated browser", () => {
  test("select → enable preview → revoke → clear → regrant unselected, with no provider requests", async ({ authenticatedPage: page }) => {
    const blockedRequests: string[] = [];
    page.on("request", (request) => {
      if (/\/api\/(health|uploads|generation|providers)|sunoapi|runway/i.test(request.url())) {
        blockedRequests.push(request.url());
      }
    });
    await page.goto("/create/health");
    const consent = page.locator(".health-consent-selection");
    const firstLabel = "Sample lab report (synthetic)";
    const secondLabel = "Sample clinic note (synthetic)";

    await expect(consent.getByRole("button", { name: "Enable private documents" })).toBeVisible();
    await expect(consent.getByText(firstLabel)).toHaveCount(0);
    await consent.getByRole("button", { name: "Enable private documents" }).click();

    await expect(consent.getByRole("button", { name: firstLabel })).toHaveAttribute("aria-pressed", "false");
    await expect(consent.getByRole("button", { name: secondLabel })).toHaveAttribute("aria-pressed", "false");
    const previews = consent.getByRole("button", { name: "Open preview" });
    await expect(previews).toHaveCount(2);
    await expect(previews.nth(0)).toBeDisabled();
    await expect(previews.nth(1)).toBeDisabled();

    await consent.getByRole("button", { name: firstLabel }).click();
    await expect(consent.getByRole("button", { name: firstLabel })).toHaveAttribute("aria-pressed", "true");
    await expect(previews.nth(0)).toBeEnabled();
    await expect(previews.nth(1)).toBeDisabled();
    await previews.nth(0).click();
    await expect(page.getByText("No preview is available yet. Nothing was uploaded or analyzed.")).toBeVisible();

    await consent.getByRole("button", { name: "Withdraw consent" }).click();
    await expect(consent.getByText(firstLabel)).toHaveCount(0);
    await expect(consent.getByText(secondLabel)).toHaveCount(0);
    await expect(previews).toHaveCount(0);
    await expect(page.getByText("No preview is available yet. Nothing was uploaded or analyzed.")).toHaveCount(0);
    await expect(consent.getByText("Consent withdrawn. Selections were cleared.")).toBeVisible();

    await consent.getByRole("button", { name: "Enable private documents" }).click();
    await expect(consent.getByRole("button", { name: firstLabel })).toHaveAttribute("aria-pressed", "false");
    await expect(previews.nth(0)).toBeDisabled();
    expect(blockedRequests).toEqual([]);
  });
});
