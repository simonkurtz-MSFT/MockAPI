import { describe, expect, it, vi } from "vitest";
import {
  createDashboardDom,
  createDashboardEventScope,
  getDashboardElements,
} from "../../src/MockAPI/wwwroot/dashboard-dom.js";

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
