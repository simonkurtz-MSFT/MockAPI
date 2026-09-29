import { describe, expect, it, vi } from "vitest";
import { createDashboardTutorialController } from "../../src/MockAPI/wwwroot/dashboard-tutorial.js";

class FakeClassList {
  constructor() {
    this.values = new Set();
  }

  add(value) {
    this.values.add(value);
  }

  remove(value) {
    this.values.delete(value);
  }

  contains(value) {
    return this.values.has(value);
  }
}

class FakeElement {
  constructor(documentRoot) {
    this.documentRoot = documentRoot;
    this.listeners = new Map();
    this.classList = new FakeClassList();
    this.controls = [];
    this.hidden = true;
    this.disabled = false;
    this.textContent = "";
    this.focusOptions = null;
    this.scrollIntoView = vi.fn();
  }

  addEventListener(name, listener) {
    if (!this.listeners.has(name)) this.listeners.set(name, new Set());
    this.listeners.get(name).add(listener);
  }

  removeEventListener(name, listener) {
    this.listeners.get(name)?.delete(listener);
  }

  dispatch(name, event = {}) {
    const dispatchedEvent = { currentTarget: this, ...event };
    for (const listener of this.listeners.get(name) ?? []) listener(dispatchedEvent);
  }

  focus(options) {
    this.documentRoot.activeElement = this;
    this.focusOptions = options;
  }

  querySelectorAll() {
    return this.controls.filter((control) => !control.disabled);
  }
}

class FakeDocument {
  constructor() {
    this.activeElement = null;
    this.listeners = new Map();
    this.targets = new Map();
  }

  addEventListener(name, listener) {
    if (!this.listeners.has(name)) this.listeners.set(name, new Set());
    this.listeners.get(name).add(listener);
  }

  removeEventListener(name, listener) {
    this.listeners.get(name)?.delete(listener);
  }

  dispatch(name, event) {
    for (const listener of this.listeners.get(name) ?? []) listener(event);
  }

  querySelector(selector) {
    if (selector === ".tutorial-highlight") {
      return [...this.targets.values()].find((target) => target.classList.contains("tutorial-highlight")) ?? null;
    }
    return this.targets.get(selector) ?? null;
  }
}

function createHarness(tutorialDismissed = false) {
  const documentRoot = new FakeDocument();
  const elements = {
    backdrop: new FakeElement(documentRoot),
    card: new FakeElement(documentRoot),
    progress: new FakeElement(documentRoot),
    title: new FakeElement(documentRoot),
    description: new FakeElement(documentRoot),
    closeButton: new FakeElement(documentRoot),
    backButton: new FakeElement(documentRoot),
    nextButton: new FakeElement(documentRoot),
    startButton: new FakeElement(documentRoot),
  };
  elements.card.controls = [elements.closeButton, elements.backButton, elements.nextButton];
  for (const selector of [".command-bar", ".statistics-workspace", ".request-log-workspace", ".workspace"]) {
    documentRoot.targets.set(selector, new FakeElement(documentRoot));
  }
  const preferencesStore = {
    get: vi.fn(() => ({ tutorialDismissed })),
    update: vi.fn(),
  };
  const onStart = vi.fn();
  const controller = createDashboardTutorialController({ documentRoot, elements, preferencesStore, onStart });
  return { controller, documentRoot, elements, onStart, preferencesStore };
}

function keyboardEvent(key, shiftKey = false) {
  return { key, shiftKey, preventDefault: vi.fn() };
}

describe("createDashboardTutorialController", () => {
  it.each([
    ["?tutorial=skip", false, false],
    ["?tutorial=skip", true, false],
    ["?tutorial=show", false, true],
    ["?tutorial=show", true, true],
    ["?tutorial=unknown", false, true],
    ["?tutorial=unknown", true, false],
    ["", false, true],
    ["", true, false],
  ])("uses launch query %s with dismissed=%s without changing preferences", (search, dismissed, shouldStart) => {
    const harness = createHarness(dismissed);

    harness.controller.startIfNeeded(search);

    expect(harness.onStart).toHaveBeenCalledTimes(shouldStart ? 1 : 0);
    expect(harness.elements.card.hidden).toBe(!shouldStart);
    expect(harness.preferencesStore.update).not.toHaveBeenCalled();
  });

  it("starts first use, navigates every step, and finishes the tutorial", () => {
    const harness = createHarness();

    harness.controller.startIfNeeded();

    expect(harness.onStart).toHaveBeenCalledOnce();
    expect(harness.elements.card.hidden).toBe(false);
    expect(harness.elements.backdrop.hidden).toBe(false);
    expect(harness.elements.progress.textContent).toBe("Step 1 of 4");
    expect(harness.elements.title.textContent).toBe("Configure your workspace");
    expect(harness.elements.description.textContent).toContain("Load examples");
    expect(harness.elements.backButton.disabled).toBe(true);
    expect(harness.elements.nextButton.focusOptions).toEqual({ preventScroll: true });
    expect(harness.documentRoot.targets.get(".command-bar").scrollIntoView).toHaveBeenCalledWith({
      block: "center",
      behavior: "instant",
    });

    harness.elements.nextButton.dispatch("click");
    harness.elements.backButton.dispatch("click");
    expect(harness.elements.progress.textContent).toBe("Step 1 of 4");

    for (let index = 0; index < 3; index += 1) harness.elements.nextButton.dispatch("click");
    expect(harness.elements.title.textContent).toBe("Manage endpoints");
    expect(harness.elements.nextButton.textContent).toBe("Finish");
    expect(harness.elements.backButton.disabled).toBe(false);

    harness.elements.nextButton.dispatch("click");
    expect(harness.elements.card.hidden).toBe(true);
    expect(harness.preferencesStore.update).toHaveBeenCalledWith({ tutorialDismissed: true });
    expect(harness.documentRoot.querySelector(".tutorial-highlight")).toBeNull();
  });

  it("skips dismissed first use and restores focus when restarted from the Help menu", () => {
    const harness = createHarness(true);

    harness.controller.startIfNeeded();
    expect(harness.onStart).not.toHaveBeenCalled();

    harness.elements.startButton.dispatch("click");
    expect(harness.elements.card.hidden).toBe(false);
    harness.elements.closeButton.dispatch("click");

    expect(harness.elements.startButton.focusOptions).toEqual({ preventScroll: true });
    expect(harness.preferencesStore.update).toHaveBeenCalledWith({ tutorialDismissed: true });
  });

  it("traps keyboard focus, handles Escape only while open, and disposes listeners", () => {
    const harness = createHarness();
    harness.controller.start();
    harness.elements.nextButton.dispatch("click");
    const shiftTab = keyboardEvent("Tab", true);
    const tab = keyboardEvent("Tab");
    const middleTab = keyboardEvent("Tab");

    harness.documentRoot.activeElement = harness.elements.closeButton;
    harness.elements.card.dispatch("keydown", shiftTab);
    expect(shiftTab.preventDefault).toHaveBeenCalledOnce();
    expect(harness.documentRoot.activeElement).toBe(harness.elements.nextButton);

    harness.elements.card.dispatch("keydown", tab);
    expect(tab.preventDefault).toHaveBeenCalledOnce();
    expect(harness.documentRoot.activeElement).toBe(harness.elements.closeButton);

    harness.documentRoot.activeElement = harness.elements.backButton;
    harness.elements.card.dispatch("keydown", middleTab);
    expect(middleTab.preventDefault).not.toHaveBeenCalled();
    harness.elements.card.dispatch("keydown", keyboardEvent("ArrowRight"));

    const unrelatedEscape = keyboardEvent("Enter");
    harness.documentRoot.dispatch("keydown", unrelatedEscape);
    expect(unrelatedEscape.preventDefault).not.toHaveBeenCalled();
    const escape = keyboardEvent("Escape");
    harness.documentRoot.dispatch("keydown", escape);
    expect(escape.preventDefault).toHaveBeenCalledOnce();
    expect(harness.elements.card.hidden).toBe(true);
    harness.documentRoot.dispatch("keydown", keyboardEvent("Escape"));

    harness.controller.start();
    harness.controller.dispose();
    expect(harness.documentRoot.querySelector(".tutorial-highlight")).toBeNull();
    expect(harness.elements.startButton.listeners.get("click").size).toBe(0);
    expect(harness.documentRoot.listeners.get("keydown").size).toBe(0);
  });
});
