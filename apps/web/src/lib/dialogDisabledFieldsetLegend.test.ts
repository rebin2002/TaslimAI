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

  it("excludes a control that escapes only the inner of two nested disabled fieldsets", () => {
    // <fieldset disabled><fieldset disabled><legend><button> - the control is
    // inside the inner fieldset's own legend, so it escapes the inner one, but
    // it is still a descendant of the outer disabled fieldset and outside that
    // fieldset's legend. The constraint applies per disabled ancestor, so the
    // outer one bars it; Chromium refuses focus.
    const outer = new FakeElement("FIELDSET").set("disabled");
    const inner = new FakeElement("FIELDSET").set("disabled");
    const innerLegend = new FakeElement("LEGEND");
    const caption = control("inner-own-legend");
    innerLegend.append(caption);
    inner.append(innerLegend);
    outer.append(inner);
    expect(evaluate(caption)).toBe(false);
  });

  it("admits a control in the outer legend that no nested disabled fieldset contains", () => {
    // <fieldset disabled><legend><button></legend><fieldset disabled> - the
    // second fieldset is a sibling subtree, so the only disabled ancestor of the
    // control is the first one, whose own legend it sits in.
    const outer = new FakeElement("FIELDSET").set("disabled");
    const outerLegend = new FakeElement("LEGEND");
    const caption = control("outer-own-legend");
    outerLegend.append(caption);
    const sibling = new FakeElement("FIELDSET").set("disabled");
    outer.append(outerLegend);
    outer.append(sibling);
    expect(evaluate(caption)).toBe(true);
  });

  it("admits a control nested deeper inside the disabled fieldset's own legend", () => {
    // The exemption is the legend subtree, not the legend's direct child list:
    // a wrapper element inside the fieldset's first legend still escapes it.
    const fieldset = new FakeElement("FIELDSET").set("disabled");
    const legend = new FakeElement("LEGEND");
    const wrapper = new FakeElement("DIV");
    const caption = control("deep-in-own-legend");
    wrapper.append(caption);
    legend.append(wrapper);
    fieldset.append(legend);
    expect(evaluate(caption)).toBe(true);
  });

  it("excludes a control three legends deep across three disabled fieldsets", () => {
    // The #1226 lane's s6 shape, measured against headless Chromium: each disabled
    // fieldset carries its own legend in the body chain, so the control escapes the
    // innermost one only. Every outer disabled ancestor applies its own bar, and the
    // outer legends - captions sitting beside the nested fieldsets - do not contain
    // it, so the browser skips it.
    const outer = new FakeElement("FIELDSET").set("disabled");
    const middle = new FakeElement("FIELDSET").set("disabled");
    const inner = new FakeElement("FIELDSET").set("disabled");
    const innerLegend = new FakeElement("LEGEND");
    const caption = control("three-deep");
    innerLegend.append(caption);
    inner.append(innerLegend);
    inner.append(control("inner-caption-holder"));
    const middleLegend = new FakeElement("LEGEND");
    middleLegend.append(new FakeElement("SPAN"));
    middle.append(middleLegend);
    middle.append(inner);
    const outerLegend = new FakeElement("LEGEND");
    outerLegend.append(new FakeElement("SPAN"));
    outer.append(outerLegend);
    outer.append(middle);
    expect(evaluate(caption)).toBe(false);
  });
});
