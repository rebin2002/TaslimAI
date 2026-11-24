import { isTabbableElement } from "./useDialogAccessibility";
import { describe, expect, it } from "vitest";

// Independent review finding on the adopted #1226 hook: the disabled-fieldset
// exemption admitted any control with *a* `legend` ancestor, but the HTML
// sequential-navigation rule rescues only the controls inside the disabled
// fieldset's own legend - the first `legend` child of that fieldset. These
// cases hold the boundary with two legends and with a nested fieldset, in the
// same dependency-free style as the adopted #1226 contract suite, so the
// exemption cannot silently widen again. The fixture below is a real DOM shape,
// not a selector stub: the hook only asks for `closest`, `parentElement`,
// `tagName`, `children` and rendering state.

class FakeElement {
  tagName: string;
  attrs = new Map<string, string>();
  children: FakeElement[] = [];
  parent: FakeElement | null = null;
  hidden = false;
  disabled = false;
  type = "";
  constructor(tagName: string, attrs: string[] = []) {
    this.tagName = tagName;
    for (const attr of attrs) this.attrs.set(attr, "");
  }
  set(attr: string, value = ""): this {
    this.attrs.set(attr, value);
    if (attr === "hidden") this.hidden = true;
    if (attr === "disabled") this.disabled = true;
    return this;
  }
  append(child: FakeElement): this {
    child.parent = this;
    this.children.push(child);
    return this;
  }
  getAttribute(name: string): string | null {
    return this.attrs.get(name) ?? null;
  }
  matches(matcher: string): boolean {
    if (matcher === "[hidden]" && this.attrs.has("hidden")) return true;
    if (matcher === '[aria-hidden="true"]' && this.attrs.get("aria-hidden") === "true") return true;
    if (matcher === "[inert]" && this.attrs.has("inert")) return true;
    if (matcher === "fieldset[disabled]" && this.tagName === "FIELDSET" && this.attrs.has("disabled")) return true;
    if (matcher === "legend" && this.tagName === "LEGEND") return true;
    return false;
  }
  closest(matcher: string): FakeElement | null {
    if (this.matches(matcher)) return this;
    return this.parent ? this.parent.closest(matcher) : null;
  }
  get parentElement(): FakeElement | null {
    return this.parent;
  }
}

function evaluate(element: FakeElement): boolean {
  // Every control in these fixtures is rendered; the finding is purely about
  // whether the disabled fieldset reaches it.
  const probe = element as unknown as HTMLElement;
  probe.checkVisibility = () => true;
  return isTabbableElement(probe, () => ({ display: "block", visibility: "visible" }));
}

function control(id: string, tagName = "BUTTON"): FakeElement {
  const element = new FakeElement(tagName);
  element.set("id", id);
  return element;
}

describe("disabled fieldset legend exemption scope", () => {
  it("admits a control inside the disabled fieldset's own first legend", () => {
    // The behaviour #1226 was written to preserve: the legend caption stays
    // reachable when its fieldset is disabled.
    const fieldset = new FakeElement("FIELDSET").set("disabled");
    const legend = new FakeElement("LEGEND");
    const caption = control("own-legend");
    legend.append(caption);
    fieldset.append(legend);
    fieldset.append(control("inside", "INPUT"));
    expect(evaluate(caption)).toBe(true);
  });

  it("excludes a control in the body of the disabled fieldset", () => {
    const fieldset = new FakeElement("FIELDSET").set("disabled");
    fieldset.append(new FakeElement("LEGEND").append(control("caption")));
    const body = control("body", "INPUT");
    fieldset.append(body);
    expect(evaluate(body)).toBe(false);
  });

  it("excludes a control inside a nested fieldset's legend", () => {
    // <fieldset disabled><fieldset><legend><button> - the inner legend belongs
    // to the inner fieldset, so the outer disabled state still applies.
    const outer = new FakeElement("FIELDSET").set("disabled");
    const inner = new FakeElement("FIELDSET");
    const innerLegend = new FakeElement("LEGEND");
    const nested = control("nested-legend");
    innerLegend.append(nested);
    inner.append(innerLegend);
    outer.append(new FakeElement("LEGEND").append(control("outer-caption")));
    outer.append(inner);
    expect(evaluate(nested)).toBe(false);
  });

  it("excludes a control inside a second legend child of the disabled fieldset", () => {
    // Only the fieldset's first legend is the sequential-navigation exemption,
    // so a stray second legend must not rescue its contents.
    const fieldset = new FakeElement("FIELDSET").set("disabled");
    fieldset.append(new FakeElement("LEGEND").append(control("first-caption")));
    const secondLegend = new FakeElement("LEGEND");
    const stray = control("second-legend");
    secondLegend.append(stray);
    fieldset.append(secondLegend);
    expect(evaluate(stray)).toBe(false);
  });

  it("applies the exemption to the nearest disabled fieldset's own legend", () => {
    // <fieldset disabled><fieldset disabled><legend><button> - the control sits
    // in the inner disabled fieldset's own legend, which is the exemption that
    // governs it; the outer fieldset does not widen or narrow it.
    const outer = new FakeElement("FIELDSET").set("disabled");
    const inner = new FakeElement("FIELDSET").set("disabled");
    const innerLegend = new FakeElement("LEGEND");
    const caption = control("inner-own-legend");
    innerLegend.append(caption);
    inner.append(innerLegend);
    outer.append(inner);
    expect(evaluate(caption)).toBe(true);
  });
});
