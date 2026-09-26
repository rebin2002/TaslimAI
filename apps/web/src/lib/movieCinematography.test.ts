import { describe, expect, it } from "vitest";
import { translate } from "./i18n";

describe("Movie Studio Shot Designer contract language", () => {
  it("keeps the normal-mode intent choices explicit", () => {
    expect(["intimate", "natural", "epic", "dynamic"].map((intent) => translate("en", `movie.intent.${intent}`))).toEqual([
      "Intimate",
      "Natural",
      "Epic",
      "Dynamic",
    ]);
  });

  it("labels advanced controls as production intent instead of native capability", () => {
    expect(translate("en", "movie.productionIntentNote")).toContain("intent fields");
    expect(translate("en", "movie.capabilityClassificationNote")).toContain("Native");
    expect(translate("en", "movie.capabilityClassificationNote")).toContain("Unsupported");
  });
});
