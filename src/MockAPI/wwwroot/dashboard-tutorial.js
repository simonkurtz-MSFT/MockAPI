"use strict";

const TUTORIAL_STEPS = [
  {
    selector: ".command-bar",
    title: "Configure your workspace",
    description:
      "Load examples, import a configuration, or create a new endpoint here. Changes are saved automatically; use Retry save if saving fails.",
  },
  {
    selector: ".statistics-workspace",
    title: "Watch response activity",
    description: "Track overall or per-endpoint attempts, response totals, throughput, and recent trends.",
  },
  {
    selector: ".request-log-workspace",
    title: "Inspect recent requests",
    description: "Review recent matched, unmatched, aborted, and failed request outcomes without retaining payloads.",
  },
  {
    selector: ".workspace",
    title: "Manage endpoints",
    description: "Filter, test, edit, enable, duplicate, and delete the endpoints that serve your mock responses.",
  },
];

/**
 * @typedef {Object} TutorialElements
 * @property {HTMLElement} backdrop Tutorial backdrop.
 * @property {HTMLElement} card Tutorial dialog card.
 * @property {HTMLElement} progress Step progress text.
 * @property {HTMLElement} title Step heading.
 * @property {HTMLElement} description Step description.
 * @property {HTMLButtonElement} closeButton Tutorial dismiss button.
 * @property {HTMLButtonElement} backButton Previous-step button.
 * @property {HTMLButtonElement} nextButton Next-step button.
 * @property {HTMLButtonElement} startButton Help-menu tutorial trigger.
 */

/**
 * @typedef {Object} DashboardTutorialController
 * @property {(search?: string) => void} startIfNeeded Starts the first-run tutorial using the URL query string's tutorial=show/skip override, otherwise the saved dismissal preference. Does not change preferences.
 * @property {(trigger?: HTMLElement|null) => void} start Starts or restarts the tutorial.
 * @property {() => void} close Closes the tutorial, persists dismissal, and restores focus.
 * @property {() => void} dispose Removes controller event listeners and visual state.
 */

/**
 * Creates the page-lifetime dashboard tutorial controller.
 *
 * @param {Object} options Controller dependencies.
 * @param {Document} options.documentRoot Document used for focus and tutorial targets.
 * @param {TutorialElements} options.elements Tutorial DOM elements.
 * @param {import("./dashboard-preferences.js").DashboardPreferencesStore} options.preferencesStore Dashboard preference store.
 * @param {() => void} options.onStart Callback that closes competing UI before the tutorial opens.
 * @returns {DashboardTutorialController} Tutorial controller.
 */
export function createDashboardTutorialController({ documentRoot, elements, preferencesStore, onStart }) {
  let stepIndex = -1;
  /** @type {HTMLElement|null} */
  let trigger = null;

  function showStep(index) {
    documentRoot.querySelector(".tutorial-highlight")?.classList.remove("tutorial-highlight");
    stepIndex = index;
    const step = TUTORIAL_STEPS[index];
    const target = documentRoot.querySelector(step.selector);
    target.classList.add("tutorial-highlight");
    target.scrollIntoView({ block: "center", behavior: "instant" });
    elements.progress.textContent = `Step ${index + 1} of ${TUTORIAL_STEPS.length}`;
    elements.title.textContent = step.title;
    elements.description.textContent = step.description;
    elements.backButton.disabled = index === 0;
    elements.nextButton.textContent = index === TUTORIAL_STEPS.length - 1 ? "Finish" : "Next";
    elements.nextButton.focus({ preventScroll: true });
  }

  /** @type {DashboardTutorialController["start"]} */
  function start(startTrigger = null) {
    onStart();
    trigger = startTrigger;
    elements.backdrop.hidden = false;
    elements.card.hidden = false;
    showStep(0);
  }

  function close() {
    documentRoot.querySelector(".tutorial-highlight")?.classList.remove("tutorial-highlight");
    elements.backdrop.hidden = true;
    elements.card.hidden = true;
    stepIndex = -1;
    preferencesStore.update({ tutorialDismissed: true });
    trigger?.focus({ preventScroll: true });
    trigger = null;
  }

  /** @param {KeyboardEvent} event Card keyboard event. */
  function handleCardKeydown(event) {
    if (event.key !== "Tab") return;
    /** @type {NodeListOf<HTMLButtonElement>} */
    const buttons = elements.card.querySelectorAll("button:not(:disabled)");
    const controls = [...buttons];
    const first = controls[0];
    const last = controls.at(-1);
    if (event.shiftKey && documentRoot.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && documentRoot.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  /** @param {KeyboardEvent} event Page keyboard event. */
  function handleDocumentKeydown(event) {
    if (event.key !== "Escape" || stepIndex < 0) return;
    event.preventDefault();
    close();
  }

  function handleStart() {
    start(elements.startButton);
  }

  function handleBack() {
    showStep(stepIndex - 1);
  }

  function handleNext() {
    if (stepIndex === TUTORIAL_STEPS.length - 1) close();
    else showStep(stepIndex + 1);
  }

  elements.startButton.addEventListener("click", handleStart);
  elements.closeButton.addEventListener("click", close);
  elements.card.addEventListener("keydown", handleCardKeydown);
  elements.backButton.addEventListener("click", handleBack);
  elements.nextButton.addEventListener("click", handleNext);
  documentRoot.addEventListener("keydown", handleDocumentKeydown);

  return Object.freeze({
    startIfNeeded(search = "") {
      const mode = new URLSearchParams(search).get("tutorial");
      if (mode === "skip") return;
      if (mode === "show") {
        start();
        return;
      }
      if (!preferencesStore.get().tutorialDismissed) start();
    },
    start,
    close,
    dispose() {
      elements.startButton.removeEventListener("click", handleStart);
      elements.closeButton.removeEventListener("click", close);
      elements.card.removeEventListener("keydown", handleCardKeydown);
      elements.backButton.removeEventListener("click", handleBack);
      elements.nextButton.removeEventListener("click", handleNext);
      documentRoot.removeEventListener("keydown", handleDocumentKeydown);
      documentRoot.querySelector(".tutorial-highlight")?.classList.remove("tutorial-highlight");
    },
  });
}
