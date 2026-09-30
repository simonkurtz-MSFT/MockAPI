(() => {
  const root = document.documentElement;
  const toggle = document.getElementById("theme-toggle");
  const status = document.getElementById("theme-status");
  toggle.hidden = false;
  toggle.addEventListener("click", () => {
    const next = root.dataset.theme === "dark" ? "light" : "dark";
    root.dataset.theme = next;
    try {
      localStorage.setItem("mockapi.site.theme", next);
      status.textContent = "";
    } catch (error) {
      console.warn("Site theme preference could not be saved.", error);
      status.textContent = "Theme changed for this visit. Browser storage is unavailable.";
    }
  });

  const gallery = document.getElementById("dashboard-gallery");
  const slides = [...gallery.querySelectorAll(".gallery-slide")];
  const choices = [...gallery.querySelectorAll("[data-slide]")];
  const galleryStatus = document.getElementById("gallery-status");
  let activeSlide = 0;

  function showSlide(index) {
    activeSlide = (index + slides.length) % slides.length;
    slides.forEach((slide, slideIndex) => {
      slide.hidden = slideIndex !== activeSlide;
    });
    choices.forEach((choice, choiceIndex) => {
      choice.setAttribute("aria-pressed", String(choiceIndex === activeSlide));
    });
    const caption = slides[activeSlide].querySelector("figcaption strong").textContent.replace(/\.$/, "");
    galleryStatus.textContent = `Screenshot ${activeSlide + 1} of ${slides.length}: ${caption}`;
  }

  document.getElementById("gallery-previous").addEventListener("click", () => showSlide(activeSlide - 1));
  document.getElementById("gallery-next").addEventListener("click", () => showSlide(activeSlide + 1));
  choices.forEach((choice, index) => choice.addEventListener("click", () => showSlide(index)));
  gallery.addEventListener("keydown", (event) => {
    if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    event.preventDefault();
    showSlide(activeSlide + (event.key === "ArrowRight" ? 1 : -1));
  });
  gallery.dataset.enhanced = "true";
  showSlide(0);
  document.getElementById("gallery-controls").hidden = false;
})();
