const ICON_PATHS = {
  chevron: '<path d="m6 9 6 6 6-6"/>',
  close: '<path d="M18 6 6 18M6 6l12 12"/>',
  copy: '<rect width="14" height="14" x="8" y="8" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/>',
  delete:
    '<path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6M10 11v6M14 11v6"/>',
  edit: '<path d="M12 20h9"/><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4Z"/>',
  help: '<circle cx="12" cy="12" r="10"/><path d="M9.1 9a3 3 0 1 1 5.8 1c0 2-3 3-3 3M12 17h.01"/>',
  play: '<path d="m6 3 14 9-14 9Z"/>',
  remove: '<circle cx="12" cy="12" r="10"/><path d="m15 9-6 6M9 9l6 6"/>',
  settings:
    '<path d="M12 15.5a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7Z"/><path d="M19.4 15a1.7 1.7 0 0 0 .34 1.88l.06.06-2.83 2.83-.06-.06a1.7 1.7 0 0 0-1.88-.34 1.7 1.7 0 0 0-1.03 1.55V21h-4v-.08A1.7 1.7 0 0 0 9 19.37a1.7 1.7 0 0 0-1.88.34l-.06.06-2.83-2.83.06-.06A1.7 1.7 0 0 0 4.63 15 1.7 1.7 0 0 0 3.08 14H3v-4h.08A1.7 1.7 0 0 0 4.63 9a1.7 1.7 0 0 0-.34-1.88l-.06-.06 2.83-2.83.06.06A1.7 1.7 0 0 0 9 4.63 1.7 1.7 0 0 0 10 3.08V3h4v.08A1.7 1.7 0 0 0 15 4.63a1.7 1.7 0 0 0 1.88-.34l.06-.06 2.83 2.83-.06.06A1.7 1.7 0 0 0 19.37 9 1.7 1.7 0 0 0 20.92 10H21v4h-.08A1.7 1.7 0 0 0 19.4 15Z"/>',
  theme:
    '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.93 4.93l1.42 1.42M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41"/>',
};

/** @typedef {keyof typeof ICON_PATHS} DashboardIcon */
/** @typedef {HTMLElementEventMap & DocumentEventMap & WindowEventMap} DashboardEventMap */
/**
 * @typedef {object} DashboardEventScope
 * @property {<T extends EventTarget, K extends keyof DashboardEventMap>(target: T, type: K,
 * handler: (event: DashboardEventMap[K] & {currentTarget: T}) => void) => void} listen Registers an owned handler with a typed receiver.
 * @property {() => void} clear Detaches all owned listeners; idempotent and reusable after a render.
 */

/**
 * @typedef {object} DashboardDom
 * @property {<T extends keyof HTMLElementTagNameMap>(tag: T, className?: string|null, text?: string) => HTMLElementTagNameMap[T]} makeElement Creates a typed HTML element with optional class and text.
 * @property {(name: DashboardIcon) => SVGSVGElement} createIcon Creates an aria-hidden decorative icon.
 * @property {(label: string, icon: DashboardIcon,
 * handler: ((event: MouseEvent & {currentTarget: HTMLButtonElement}) => void)|null, tone?: string) => HTMLButtonElement} actionButton
 * Creates a labeled button; null delegates clicks to a parent instead of registering a listener.
 */

/**
 * Collects a feature's required elements, checking their declared HTML tags before narrowing their types.
 * @template {Record<string, keyof HTMLElementTagNameMap>} T
 * @param {Document} documentRoot Dashboard document.
 * @param {T} tags Required element IDs mapped to their expected tag names.
 * @returns {{[K in keyof T]: HTMLElementTagNameMap[T[K]]}} Precisely typed elements indexed by ID.
 * @throws {Error} A required element is missing or has the wrong tag.
 */
export function getDashboardElements(documentRoot, tags) {
  const entries = Object.entries(tags).map(([id, tag]) => {
    const element = documentRoot.getElementById(id);
    if (!element) throw new Error(`Missing dashboard element: ${id}`);
    if (element.localName !== tag) throw new Error(`Dashboard element ${id} must be <${tag}>.`);
    return [id, element];
  });
  // Every entry was checked above; Object.fromEntries does not retain that key/tag relationship.
  return /** @type {{[K in keyof T]: HTMLElementTagNameMap[T[K]]}} */ (Object.fromEntries(entries));
}

/**
 * Owns listeners for a controller or one render. Clearing is idempotent and permits reuse.
 * @returns {DashboardEventScope} Listener scope.
 */
export function createDashboardEventScope() {
  /** @type {{target: EventTarget, type: string, handler: EventListener}[]} */
  const listeners = [];
  return {
    listen(target, type, handler) {
      target.addEventListener(type, handler);
      listeners.push({ target, type, handler });
    },
    clear() {
      for (const { target, type, handler } of listeners.splice(0)) target.removeEventListener(type, handler);
    },
  };
}

/**
 * Creates small DOM helpers without accessing a global document.
 * @param {Document} document Dashboard document.
 * @param {DashboardEventScope} events Owner of action-button listeners.
 * @returns {DashboardDom} Typed DOM helpers; they never inject operator-controlled HTML.
 */
export function createDashboardDom(document, events) {
  /**
   * @template {keyof HTMLElementTagNameMap} T
   * @param {T} tag HTML tag.
   * @param {string|null} [className] Optional class names.
   * @param {string} [text] Optional plain text.
   * @returns {HTMLElementTagNameMap[T]} Created element.
   */
  function makeElement(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  }

  /** @param {DashboardIcon} name Decorative icon name. */
  function createIcon(name) {
    const icon = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    icon.setAttribute("viewBox", "0 0 24 24");
    icon.setAttribute("aria-hidden", "true");
    icon.setAttribute("focusable", "false");
    icon.innerHTML = ICON_PATHS[name];
    return icon;
  }

  /** @type {DashboardDom["actionButton"]} */
  function actionButton(label, icon, handler, tone = "neutral") {
    const button = makeElement("button", `icon-button icon-${tone}`);
    button.type = "button";
    button.title = label;
    button.setAttribute("aria-label", label);
    button.append(createIcon(icon));
    if (handler) events.listen(button, "click", handler);
    return button;
  }
  return { makeElement, createIcon, actionButton };
}
