import { describe, expect, it, vi } from "vitest";
import {
  bindDashboardTooltip,
  createDashboardDom,
  createDashboardEventScope,
  getDashboardElements,
} from "../../src/MockAPI/wwwroot/dashboard-dom.js";

describe("shared information tooltips", () => {
  function createTooltipFixture({
    top = 100,
    bottom = 126,
    right = 200,
    height = 120,
    width = 360,
    viewportHeight = 800,
    onActivate,
  } = {}) {
    const trigger = Object.assign(new EventTarget(), {
      ownerDocument: { defaultView: { innerHeight: viewportHeight, innerWidth: 1000 } },
      getBoundingClientRect: () => ({ top, bottom, right }),
      matches: vi.fn(() => false),
    });
    const tooltip = {
      style: {},
      getBoundingClientRect: () => ({ height, width }),
      showPopover: vi.fn(),
      hidePopover: vi.fn(),
    };
    const events = createDashboardEventScope();
    const preview = bindDashboardTooltip({ trigger, tooltip, events, onActivate });
    return { trigger, tooltip, events, preview };
  }

  it("opens on hover, focus, or touch activation and hides on pointer departure or blur", () => {
    const { trigger, tooltip, preview } = createTooltipFixture();
    trigger.dispatchEvent(new Event("mouseenter"));
    expect(tooltip.showPopover).toHaveBeenCalledTimes(1);
    expect(tooltip.style).toEqual({ top: "126px", left: "8px", maxHeight: "400px" });
    trigger.dispatchEvent(new Event("mouseleave"));
    expect(tooltip.hidePopover).toHaveBeenCalledTimes(1);
    trigger.dispatchEvent(new Event("focus"));
    trigger.dispatchEvent(new Event("click"));
    expect(tooltip.showPopover).toHaveBeenCalledTimes(3);
    trigger.matches.mockReturnValue(true);
    trigger.dispatchEvent(new Event("blur"));
    expect(tooltip.hidePopover).toHaveBeenCalledTimes(1);
    trigger.matches.mockReturnValue(false);
    trigger.dispatchEvent(new Event("blur"));
    expect(tooltip.hidePopover).toHaveBeenCalledTimes(2);
    preview.hide();
    expect(tooltip.hidePopover).toHaveBeenCalledTimes(3);
  });

  it("places tall previews above their trigger and keeps them within the right viewport edge", () => {
    const { trigger, tooltip } = createTooltipFixture({ top: 700, bottom: 726, right: 1100, height: 200 });
    trigger.dispatchEvent(new Event("mouseenter"));
    expect(tooltip.style).toEqual({ top: "500px", left: "632px", maxHeight: "400px" });
  });

  it("uses the larger space below for an oversized preview and limits its height", () => {
    const { trigger, tooltip } = createTooltipFixture({ top: 300, bottom: 326, right: 600, height: 700 });
    trigger.dispatchEvent(new Event("focus"));
    expect(tooltip.style).toEqual({ top: "326px", left: "240px", maxHeight: "400px" });
  });

  it("limits previews to sixty percent of a short viewport", () => {
    const { trigger, tooltip } = createTooltipFixture({ viewportHeight: 500 });
    trigger.dispatchEvent(new Event("focus"));
    expect(tooltip.style.maxHeight).toBe("300px");
  });

  it("limits previews to the available space beside the trigger", () => {
    const { trigger, tooltip } = createTooltipFixture({
      top: 280,
      bottom: 306,
      viewportHeight: 600,
      height: 500,
    });
    trigger.dispatchEvent(new Event("focus"));
    expect(tooltip.style).toEqual({ top: "306px", left: "8px", maxHeight: "286px" });
  });

  it("activates the editor without reopening help when focus is restored", () => {
    const onActivate = vi.fn();
    const { trigger, tooltip, events } = createTooltipFixture({ onActivate });
    trigger.dispatchEvent(new Event("click"));
    expect(onActivate).toHaveBeenCalledTimes(1);
    expect(tooltip.hidePopover).toHaveBeenCalledTimes(1);
    trigger.dispatchEvent(new Event("focus"));
    expect(tooltip.showPopover).not.toHaveBeenCalled();
    trigger.dispatchEvent(new Event("focus"));
    expect(tooltip.showPopover).toHaveBeenCalledTimes(1);
    events.clear();
    for (const name of ["mouseenter", "focus", "mouseleave", "blur", "click"]) {
      trigger.dispatchEvent(new Event(name));
    }
    expect(tooltip.showPopover).toHaveBeenCalledTimes(1);
    expect(tooltip.hidePopover).toHaveBeenCalledTimes(2);
    expect(onActivate).toHaveBeenCalledTimes(1);
  });
});

describe("dashboard event ownership", () => {
  it("detaches owned handlers without removing another controller's handlers and can be reused after clearing", () => {
    const target = new EventTarget();
    const owned = vi.fn();
    const other = vi.fn();
    const events = createDashboardEventScope();
    target.addEventListener("change", other);
    events.listen(target, "change", owned);
    target.dispatchEvent(new Event("change"));
    expect(owned).toHaveBeenCalledTimes(1);

    events.clear();
    events.clear();
    target.dispatchEvent(new Event("change"));
    expect(owned).toHaveBeenCalledTimes(1);
    expect(other).toHaveBeenCalledTimes(2);

    events.listen(target, "change", owned);
    target.dispatchEvent(new Event("change"));
    expect(owned).toHaveBeenCalledTimes(2);
    events.clear();
  });
});

describe("dashboard DOM helpers", () => {
  function element() {
    return Object.assign(new EventTarget(), { setAttribute: vi.fn(), append: vi.fn() });
  }

  it("requires each feature's declared DOM elements", () => {
    const control = Object.assign(element(), { localName: "button" });
    const document = { getElementById: vi.fn(() => control) };
    expect(getDashboardElements(document, { control: "button" })).toEqual({ control });
    document.getElementById.mockReturnValue(null);
    expect(() => getDashboardElements(document, { missing: "input" })).toThrow("missing");
  });

  it("rejects the wrong control tag instead of returning a misleading element type", () => {
    const document = { getElementById: () => ({ localName: "div" }) };
    expect(() => getDashboardElements(document, { control: "input" })).toThrow("control must be <input>");
  });

  it("builds text nodes, icons, and lifecycle-owned action buttons", () => {
    const document = { createElement: vi.fn(element), createElementNS: vi.fn(element) };
    const events = createDashboardEventScope();
    const dom = createDashboardDom(document, events);
    const plain = dom.makeElement("div");
    expect(plain.className).toBeUndefined();
    expect(plain.textContent).toBeUndefined();
    expect(dom.makeElement("span", "label", "Name")).toMatchObject({ className: "label", textContent: "Name" });
    const icon = dom.createIcon("edit");
    expect(icon.innerHTML).toContain("<path");
    expect(icon.setAttribute).toHaveBeenCalledWith("aria-hidden", "true");
    const action = vi.fn();
    const button = dom.actionButton("Edit", "edit", action);
    expect(button).toMatchObject({ type: "button", title: "Edit", className: "icon-button icon-neutral" });
    expect(button.setAttribute).toHaveBeenCalledWith("aria-label", "Edit");
    expect(dom.actionButton("Delete", "delete", action, "delete").className).toBe("icon-button icon-delete");
    dom.actionButton("Remove", "remove", null).dispatchEvent(new Event("click"));
    expect(action).not.toHaveBeenCalled();
    button.dispatchEvent(new Event("click"));
    expect(action).toHaveBeenCalledTimes(1);
    events.clear();
    button.dispatchEvent(new Event("click"));
    expect(action).toHaveBeenCalledTimes(1);
  });
});
